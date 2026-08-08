namespace PrintAgent.Host.Storage;

/// <summary>Conteúdo de <c>pending/&lt;jobId&gt;.json</c> (plano §7.1): job recebido, ainda não confirmado como impresso.</summary>
public sealed record PendingJobRecord(
    string JobId, string PayloadJson, DateTimeOffset ReceivedAt, int Attempts, DateTimeOffset NextAttemptAt, string? LastError);

/// <summary>Conteúdo de <c>printed/&lt;jobId&gt;.json</c> (plano §7.1): impresso; carrega o próprio estado de ack.</summary>
public sealed record PrintedJobRecord(
    string JobId, DateTimeOffset PrintedAt, int Attempts, bool Acked, DateTimeOffset? LastAckAttemptAt, string? LastAckError);

/// <summary>Conteúdo de <c>failed/&lt;jobId&gt;.json</c> (plano §7.1): retry local esgotado; carrega o próprio estado de ack.</summary>
public sealed record FailedJobRecord(
    string JobId, DateTimeOffset FailedAt, int Attempts, string? ErrorCode, string? ErrorMessage,
    bool Acked, DateTimeOffset? LastAckAttemptAt, string? LastAckError);
