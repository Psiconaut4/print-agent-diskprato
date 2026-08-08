namespace PrintAgent.Host.Storage;

/// <summary>Linha da tabela <c>jobs</c> (plano §7.1): job recebido, ainda não confirmado como impresso.</summary>
public sealed record QueuedJob(string JobId, string PayloadJson, DateTimeOffset ReceivedAt, int Attempts, string? LastError);

/// <summary>Linha da tabela <c>pending_acks</c> (plano §7.1): ack que não conseguiu sair ainda.</summary>
public sealed record PendingAckRecord(string JobId, string BodyJson, int Attempts);
