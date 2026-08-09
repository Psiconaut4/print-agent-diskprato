using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PrintAgent.Contracts;

namespace PrintAgent.Transport.Sse;

/// <summary>
/// Cliente do stream SSE de eventos de impressão (§6.2-§6.4). Mantém a
/// conexão, faz o parsing linha-a-linha dos frames `id:`/`event:`/`data:`,
/// reconecta com backoff+jitter, e aplica o watchdog de 90s.
///
/// Decisões de design (a área tem várias regras implícitas no plano que
/// exigiram uma escolha explícita):
///
/// - <c>lastEventId</c> só avança automaticamente para eventos que não têm
///   necessidade de durabilidade (`connected`, `ping`, `shutdown`): não há
///   nada que o consumidor precise persistir antes de "confirmar" esses
///   eventos, então adiá-los não protegeria nada. Para `print:job` e
///   `print:job:cancelled`, o avanço só acontece quando o consumidor chama
///   <see cref="MarkProcessed"/> explicitamente, exatamente como o plano
///   pede ("lastEventId só avança depois que o consumidor confirma que
///   processou com sucesso").
/// - O watchdog é resetado a cada frame completo recebido (linha em
///   branco), não só em `ping`. Qualquer frame prova que a conexão está
///   viva; `ping` é o heartbeat garantido a cada 30s, mas não é o único
///   sinal de vida possível.
/// - Erros de deserialização de payload (JSON malformado dentro de um
///   evento) não derrubam o loop: o evento é ignorado e o stream continua.
///   Preferimos perder um evento pontual a matar a conexão inteira por um
///   payload ruim.
/// - Eventos SSE desconhecidos (extensões futuras do backend) são
///   ignorados silenciosamente, para compatibilidade futura (mesma regra de
///   "aceitar campos/valores novos sem quebrar" do §9 do plano).
/// </summary>
public sealed class SseStreamClient
{
    public const string StreamPath = "/api/print-agents/v1/stream";

    private static readonly TimeSpan DefaultWatchdogTimeout = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _watchdogTimeout;
    private readonly RetryBackoffCalculator _backoff;

    private string? _lastEventId;
    private int _attempt;

    public SseStreamClient(
        HttpClient streamHttpClient,
        TimeProvider? timeProvider = null,
        TimeSpan? watchdogTimeout = null,
        RetryBackoffCalculator? backoff = null)
    {
        _http = streamHttpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _watchdogTimeout = watchdogTimeout ?? DefaultWatchdogTimeout;
        _backoff = backoff ?? new RetryBackoffCalculator();
    }

    /// <summary>Handshake inicial (`connected`). O consumidor deve disparar `jobs/pending` aqui (§6.4).</summary>
    public event Action<string>? Connected;

    /// <summary>`print:job`. Repasse: dedup/persistência/impressão ficam a cargo do consumidor (Host).</summary>
    public event Action<SseJobEvent>? JobReceived;

    /// <summary>`print:job:cancelled`.</summary>
    public event Action<SseJobCancelledEvent>? JobCancelled;

    /// <summary>401 ou `device:revoked`: o loop para de reconectar depois deste evento.</summary>
    public event Action<TokenInvalidReason>? TokenInvalidated;

    /// <summary>400 com PRINT_AGENT_VERSION_UNSUPPORTED ao abrir o stream.</summary>
    public event Action? VersionUnsupported;

    public string? LastEventId => _lastEventId;

    /// <summary>
    /// Confirma que o evento com este id foi processado com sucesso
    /// (persistido/impresso). Só depois disso o próximo `Last-Event-ID`
    /// enviado numa reconexão reflete este evento.
    /// </summary>
    public void MarkProcessed(string eventId) => _lastEventId = eventId;

    /// <summary>
    /// Roda o loop de conexão até <paramref name="ct"/> ser cancelado ou até
    /// um evento terminal (401, `device:revoked`, versão não suportada)
    /// mandar parar de reconectar.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var outcome = await ConnectAndPumpAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                switch (outcome.Kind)
                {
                    case OutcomeKind.StopReconnecting:
                        return;

                    case OutcomeKind.ReconnectImmediately:
                        _attempt = 0;
                        break;

                    case OutcomeKind.WaitThenReconnect:
                        var delay = outcome.ExplicitDelay ?? _backoff.Next(++_attempt);
                        await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // encerramento normal.
        }
    }

    private async Task<ConnectionOutcome> ConnectAndPumpAsync(CancellationToken outerCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        using var watchdog = _timeProvider.CreateTimer(
            _ => linkedCts.Cancel(),
            null,
            _watchdogTimeout,
            Timeout.InfiniteTimeSpan);

        using var request = new HttpRequestMessage(HttpMethod.Get, StreamPath);
        if (_lastEventId is not null) request.Headers.Add("Last-Event-ID", _lastEventId);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            // watchdog disparou antes dos headers chegarem, ou timeout de conexão.
            return ConnectionOutcome.WaitAndReconnect();
        }
        catch (HttpRequestException)
        {
            return ConnectionOutcome.WaitAndReconnect();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                TokenInvalidated?.Invoke(TokenInvalidReason.Unauthorized);
                return ConnectionOutcome.Stop();
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var code = await TryReadErrorCodeAsync(response, outerCt).ConfigureAwait(false);
                if (code == ApiErrorCode.PRINT_AGENT_VERSION_UNSUPPORTED)
                {
                    VersionUnsupported?.Invoke();
                }

                return ConnectionOutcome.Stop();
            }

            if ((int)response.StatusCode == 429)
            {
                var retryAfter = RetryAfterHelper.TryGet(response);
                return ConnectionOutcome.WaitAndReconnect(retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ConnectionOutcome.WaitAndReconnect();
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(outerCt).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? id = null;
                string? evtName = null;
                var data = new StringBuilder();

                while (!linkedCts.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (line is null) break; // servidor fechou o stream

                    if (line.Length == 0)
                    {
                        // frame completo: qualquer frame prova que a conexão está viva.
                        watchdog.Change(_watchdogTimeout, Timeout.InfiniteTimeSpan);

                        var terminal = Dispatch(id, evtName, data.ToString());
                        id = null;
                        evtName = null;
                        data.Clear();

                        if (terminal is { } outcome) return outcome;
                        continue;
                    }

                    if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
                    else if (line.StartsWith("event: ", StringComparison.Ordinal)) evtName = line[7..];
                    else if (line.StartsWith("data: ", StringComparison.Ordinal)) data.Append(line[6..]);
                }
            }
            catch (IOException)
            {
                // conexao caiu no meio da leitura (reset, "connection aborted" etc.) —
                // mesmo tratamento de uma desconexao normal: reconectar com backoff,
                // nunca deixar subir e derrubar o Worker/host inteiro.
                return ConnectionOutcome.WaitAndReconnect();
            }
            catch (HttpRequestException)
            {
                return ConnectionOutcome.WaitAndReconnect();
            }
        }

        return ConnectionOutcome.WaitAndReconnect();
    }

    private ConnectionOutcome? Dispatch(string? id, string? evtName, string data)
    {
        switch (evtName)
        {
            case "connected":
                _attempt = 0;
                if (id is not null) _lastEventId = id;
                var connectedPayload = Deserialize<SseConnectedPayload>(data);
                Connected?.Invoke(connectedPayload?.DeviceId ?? string.Empty);
                return null;

            case "ping":
                if (id is not null) _lastEventId = id;
                return null;

            case "print:job":
                var job = Deserialize<PrintJob>(data);
                if (job is not null) JobReceived?.Invoke(new SseJobEvent(job, id));
                return null;

            case "print:job:cancelled":
                var cancelled = Deserialize<SseJobCancelledPayload>(data);
                if (cancelled is not null)
                {
                    JobCancelled?.Invoke(new SseJobCancelledEvent(cancelled.JobId, cancelled.OrderId, id));
                }

                return null;

            case "device:revoked":
                TokenInvalidated?.Invoke(TokenInvalidReason.DeviceRevoked);
                return ConnectionOutcome.Stop();

            case "shutdown":
                if (id is not null) _lastEventId = id;
                return ConnectionOutcome.ReconnectNow();

            default:
                return null; // evento desconhecido: ignora, compatibilidade futura.
        }
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private enum OutcomeKind
    {
        WaitThenReconnect,
        ReconnectImmediately,
        StopReconnecting,
    }

    private readonly record struct ConnectionOutcome(OutcomeKind Kind, TimeSpan? ExplicitDelay = null)
    {
        public static ConnectionOutcome WaitAndReconnect(TimeSpan? explicitDelay = null) =>
            new(OutcomeKind.WaitThenReconnect, explicitDelay);

        public static ConnectionOutcome ReconnectNow() => new(OutcomeKind.ReconnectImmediately);

        public static ConnectionOutcome Stop() => new(OutcomeKind.StopReconnecting);
    }
}
