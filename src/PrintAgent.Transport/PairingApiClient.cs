using System.Net.Http.Json;
using System.Text.Json;
using PrintAgent.Contracts;

namespace PrintAgent.Transport;

/// <summary>
/// Resultado de uma tentativa de pareamento. Código inválido, expirado ou já
/// usado retornam todos <see cref="ApiErrorCode.PRINT_AGENT_PAIRING_CODE_INVALID"/>
/// — a UI mostra uma mensagem só, sem tentar inferir qual dos três foi
/// (§6.1). É por isso que <see cref="Failure"/> carrega o code cru em vez de
/// um enum próprio com mais casos.
/// </summary>
public abstract record PairOutcome
{
    private PairOutcome()
    {
    }

    public sealed record Success(PairResponse Response) : PairOutcome;

    public sealed record Failure(ApiErrorCode? Code, string Message) : PairOutcome;
}

/// <summary>
/// Cliente para <c>POST /api/print-agents/v1/pair</c> (§6.1). Não usa
/// Authorization: o código de pareamento é a própria credencial.
/// </summary>
public sealed class PairingApiClient
{
    private const string PairPath = "/api/print-agents/v1/pair";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public PairingApiClient(HttpClient apiHttpClient)
    {
        _http = apiHttpClient;
    }

    public async Task<PairOutcome> PairAsync(
        string code,
        string deviceName,
        string agentVersion,
        string platform,
        CancellationToken ct = default)
    {
        var request = new PairRequest
        {
            Code = PairingCodeNormalizer.Normalize(code),
            DeviceName = deviceName,
            AgentVersion = agentVersion,
            Platform = platform,
        };

        using var response = await _http.PostAsJsonAsync(PairPath, request, JsonOptions, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<PairResponse>(JsonOptions, ct).ConfigureAwait(false);
            return body is null
                ? new PairOutcome.Failure(null, "Resposta vazia do servidor ao parear.")
                : new PairOutcome.Success(body);
        }

        ApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // corpo de erro pode não ser JSON válido; segue com mensagem genérica abaixo.
        }

        return new PairOutcome.Failure(
            error?.Code,
            error?.Message ?? $"Falha ao parear (HTTP {(int)response.StatusCode}).");
    }
}
