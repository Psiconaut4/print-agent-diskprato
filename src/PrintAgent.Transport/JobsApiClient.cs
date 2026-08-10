using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintAgent.Contracts;

namespace PrintAgent.Transport;

/// <summary>
/// Resultado de um ack. `NotFound` cobre "job não existe mais" (§6.6: 404 no
/// ack → descartar da fila local, sem retry). Qualquer outro caso terminal
/// vira exceção (401, 400 versão não suportada).
/// </summary>
public enum AckOutcome
{
    Acknowledged,
    JobNotFound,
}

/// <summary>
/// Cliente HTTP das rotas de dispositivo além do stream: `jobs/pending`,
/// `ack` e `status` (§6.5, §6.6). Usa o HttpClient "curto" (timeout normal),
/// diferente do usado pelo <see cref="Sse.SseStreamClient"/>.
///
/// Retry: 5xx e erro de rede retry indefinidamente com backoff+jitter; 429
/// respeita `Retry-After` quando presente; 401 vira
/// <see cref="PrintAgentUnauthorizedException"/> (a camada Transport não
/// entra em loop nem apaga token — só sinaliza); 404 no ack é terminal sem
/// retry; 400 com PRINT_AGENT_VERSION_UNSUPPORTED vira
/// <see cref="PrintAgentVersionUnsupportedException"/>.
/// </summary>
public sealed class JobsApiClient
{
    private const string PendingPath = "/api/print-agents/v1/jobs/pending";
    private const string StatusPath = "/api/print-agents/v1/status";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        // `AckRequest`/`ReportStatusDto` tem varios campos opcionais
        // (errorCode, errorMessage, transport, ...). O schema Zod do backend
        // usa `.optional()`, nao `.nullable()` — aceita a chave ausente mas
        // rejeita `null` explicito. Sem isso, o serializer padrao do
        // System.Text.Json escreve `"errorCode":null` para toda propriedade
        // nula do DTO, e o ack de um job impresso com sucesso (sem erro)
        // sempre falha a validacao com 400.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new UtcZDateTimeOffsetConverter());
        options.Converters.Add(new UtcZNullableDateTimeOffsetConverter());
        return options;
    }

    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly RetryBackoffCalculator _backoff;

    public JobsApiClient(HttpClient apiHttpClient, TimeProvider? timeProvider = null, RetryBackoffCalculator? backoff = null)
    {
        _http = apiHttpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _backoff = backoff ?? new RetryBackoffCalculator();
    }

    /// <summary>
    /// GET /jobs/pending. Chamado a cada (re)conexão do stream — é a rede de
    /// segurança real contra perda de pedido (§6.2), não o replay do SSE.
    /// </summary>
    public async Task<IReadOnlyList<PrintJob>> GetPendingJobsAsync(int? limit = null, CancellationToken ct = default)
    {
        var url = limit is { } l ? $"{PendingPath}?limit={l}" : PendingPath;

        using var response = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct).ConfigureAwait(false);

        var parsed = await response.Content.ReadFromJsonAsync<Response>(JsonOptions, ct).ConfigureAwait(false);
        return parsed?.Jobs?.ToList() ?? new List<PrintJob>();
    }

    /// <summary>
    /// POST /jobs/{jobId}/ack. Só deve ser chamado depois que os bytes
    /// saíram com sucesso pelo transporte (impresso) ou depois de esgotar o
    /// retry local (failed) — essa decisão é do chamador (Host), não desta
    /// camada (§6.5).
    /// </summary>
    public async Task<AckOutcome> AckJobAsync(string jobId, AckRequest ack, CancellationToken ct = default)
    {
        var path = $"/api/print-agents/v1/jobs/{Uri.EscapeDataString(jobId)}/ack";
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsJsonAsync(path, ack, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                await DelayAsync(_backoff.Next(++attempt), ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return AckOutcome.JobNotFound;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new PrintAgentUnauthorizedException();
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    var code = await TryReadErrorCodeAsync(response, ct).ConfigureAwait(false);
                    if (code == ApiErrorCode.PRINT_AGENT_VERSION_UNSUPPORTED)
                    {
                        throw new PrintAgentVersionUnsupportedException();
                    }

                    response.EnsureSuccessStatusCode(); // 400 inesperado: deixa estourar
                }

                if ((int)response.StatusCode == 429)
                {
                    var wait = RetryAfterHelper.TryGet(response) ?? _backoff.Next(++attempt);
                    await DelayAsync(wait, ct).ConfigureAwait(false);
                    continue;
                }

                if ((int)response.StatusCode >= 500)
                {
                    await DelayAsync(_backoff.Next(++attempt), ct).ConfigureAwait(false);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return AckOutcome.Acknowledged;
            }
        }
    }

    /// <summary>
    /// POST /status. Best-effort por definição do contrato: nunca é
    /// pré-requisito para imprimir, então nunca bloqueia nem propaga falha —
    /// apenas tenta uma vez e desiste silenciosamente.
    /// </summary>
    public async Task ReportStatusAsync(StatusReport report, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(StatusPath, report, JsonOptions, ct).ConfigureAwait(false);
            // best-effort: não inspeciona o resultado além de não lançar.
        }
        catch (Exception)
        {
            // best-effort: erro de rede (ou qualquer outro) aqui nunca deve
            // atrapalhar a impressão nem propagar para o chamador.
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage response;
            try
            {
                response = await send().ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                await DelayAsync(_backoff.Next(++attempt), ct).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new PrintAgentUnauthorizedException();
            }

            if ((int)response.StatusCode == 429)
            {
                var wait = RetryAfterHelper.TryGet(response) ?? _backoff.Next(++attempt);
                response.Dispose();
                await DelayAsync(wait, ct).ConfigureAwait(false);
                continue;
            }

            if ((int)response.StatusCode >= 500)
            {
                response.Dispose();
                await DelayAsync(_backoff.Next(++attempt), ct).ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return response;
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, _timeProvider, ct);

    private static async Task<ApiErrorCode?> TryReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, ct).ConfigureAwait(false);
            return error?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
