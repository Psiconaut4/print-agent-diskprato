using System.Text.Json.Serialization;
using PrintAgent.Contracts;

namespace PrintAgent.Transport.Sse;

/// <summary>Motivo pelo qual o token de dispositivo deixou de ser válido (§6.4, §6.6).</summary>
public enum TokenInvalidReason
{
    /// <summary>401 em qualquer rota de dispositivo.</summary>
    Unauthorized,

    /// <summary>Evento `device:revoked` recebido pelo stream.</summary>
    DeviceRevoked,
}

/// <summary>Job de impressão recebido pelo stream, com o id do frame SSE que o carregou.</summary>
/// <param name="Job">O payload `PrintJob` desserializado.</param>
/// <param name="EventId">
/// Id do frame SSE (`id:`), ou null se o backend não mandou um. Repasse para
/// <see cref="SseStreamClient.MarkProcessed"/> depois que o job for
/// persistido/impresso com sucesso — nunca antes.
/// </param>
public sealed record SseJobEvent(PrintJob Job, string? EventId);

/// <summary>Cancelamento de job recebido pelo stream (`print:job:cancelled`).</summary>
public sealed record SseJobCancelledEvent(string JobId, string OrderId, string? EventId);

internal sealed class SseConnectedPayload
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }
}

internal sealed class SseJobCancelledPayload
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("orderId")]
    public string OrderId { get; set; } = string.Empty;
}
