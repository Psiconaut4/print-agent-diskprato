using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PrintAgent.Host.Storage;

/// <summary>
/// Fila local em SQLite (plano §7.1) —
/// <c>%ProgramData%\DiskPrato\PrintAgent\agent.db</c>. Sobrevive a reboot da
/// máquina da loja, o que o replay do SSE (TTL de 5 min) não cobre.
///
/// Uma única conexão persistente em modo WAL: processo único, único
/// escritor, sem motivo para abrir/fechar conexão a cada chamada. As
/// operações são deliberadamente síncronas — são leituras/escritas locais em
/// um arquivo pequeno, não vale a pena a complexidade de uma API assíncrona
/// só por convenção.
/// </summary>
public sealed class JobStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public JobStore(string? path = null)
    {
        var dbPath = path ?? Path.Combine(Config.AgentConfigStore.DefaultDirectory, "agent.db");
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS jobs (
              job_id          TEXT PRIMARY KEY,
              payload_json    TEXT NOT NULL,
              received_at     TEXT NOT NULL,
              attempts        INTEGER NOT NULL DEFAULT 0,
              next_attempt_at TEXT NOT NULL,
              last_error      TEXT
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS printed (
              job_id     TEXT PRIMARY KEY,
              printed_at TEXT NOT NULL,
              acked      INTEGER NOT NULL DEFAULT 0
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS pending_acks (
              job_id     TEXT PRIMARY KEY,
              body_json  TEXT NOT NULL,
              attempts   INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    /// <summary>
    /// Já foi impresso (independente de já ter sido confirmado por ack). O
    /// mesmo job chega pelo stream e por <c>jobs/pending</c> — este é o
    /// ponto de dedup (plano §6.2/§7.1), não é otimização, é correção.
    /// </summary>
    public bool IsAlreadyPrinted(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM printed WHERE job_id = $jobId LIMIT 1;";
        cmd.Parameters.AddWithValue("$jobId", jobId);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Grava o job recebido antes de qualquer outra coisa (plano §7.1: "commit
    /// em jobs antes de responder qualquer coisa"). Idempotente — reenvio do
    /// mesmo jobId não duplica nem reseta tentativas já feitas.
    /// </summary>
    public void RecordReceived(string jobId, string payloadJson, DateTimeOffset receivedAt)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (job_id, payload_json, received_at, attempts, next_attempt_at, last_error)
            VALUES ($jobId, $payload, $receivedAt, 0, $receivedAt, NULL)
            ON CONFLICT(job_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.Parameters.AddWithValue("$payload", payloadJson);
        cmd.Parameters.AddWithValue("$receivedAt", Format(receivedAt));
        cmd.ExecuteNonQuery();
    }

    public void RemoveFromQueue(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE job_id = $jobId;";
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Marca como impresso e tira da fila de retry — as duas coisas na mesma transação.</summary>
    public void RecordPrinted(string jobId, DateTimeOffset printedAt)
    {
        using var transaction = _connection.BeginTransaction();

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO printed (job_id, printed_at, acked) VALUES ($jobId, $printedAt, 0)
                ON CONFLICT(job_id) DO UPDATE SET printed_at = excluded.printed_at;
                """;
            insert.Parameters.AddWithValue("$jobId", jobId);
            insert.Parameters.AddWithValue("$printedAt", Format(printedAt));
            insert.ExecuteNonQuery();
        }

        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM jobs WHERE job_id = $jobId;";
            delete.Parameters.AddWithValue("$jobId", jobId);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void MarkAcked(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE printed SET acked = 1 WHERE job_id = $jobId;";
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Registra uma tentativa de impressão que falhou e agenda a próxima (plano §6.5).</summary>
    public void RecordAttemptFailure(string jobId, string? error, DateTimeOffset nextAttemptAt)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE jobs
            SET attempts = attempts + 1, next_attempt_at = $nextAttemptAt, last_error = $error
            WHERE job_id = $jobId;
            """;
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.Parameters.AddWithValue("$nextAttemptAt", Format(nextAttemptAt));
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Jobs cujo próximo horário de tentativa já chegou, mais antigos primeiro.</summary>
    public IReadOnlyList<QueuedJob> GetDueJobs(DateTimeOffset now)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT job_id, payload_json, received_at, attempts, last_error
            FROM jobs
            WHERE next_attempt_at <= $now
            ORDER BY received_at ASC;
            """;
        cmd.Parameters.AddWithValue("$now", Format(now));

        var result = new List<QueuedJob>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new QueuedJob(
                reader.GetString(0),
                reader.GetString(1),
                Parse(reader.GetString(2)),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    /// <summary>Tamanho atual da fila de retry — vira <c>StatusReport.queuedJobs</c> (plano §6, best-effort).</summary>
    public int GetQueueLength()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM jobs;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void EnqueuePendingAck(string jobId, string bodyJson)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pending_acks (job_id, body_json, attempts) VALUES ($jobId, $body, 0)
            ON CONFLICT(job_id) DO UPDATE SET body_json = excluded.body_json;
            """;
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.Parameters.AddWithValue("$body", bodyJson);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PendingAckRecord> GetPendingAcks()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT job_id, body_json, attempts FROM pending_acks;";

        var result = new List<PendingAckRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PendingAckRecord(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return result;
    }

    public void RemovePendingAck(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_acks WHERE job_id = $jobId;";
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.ExecuteNonQuery();
    }

    public void IncrementAckAttempt(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE pending_acks SET attempts = attempts + 1 WHERE job_id = $jobId;";
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Limpa <c>printed</c> com mais de 7 dias — janela folgada sobre as 24h que <c>jobs/pending</c> cobre (plano §7.1).</summary>
    public void CleanupOldPrinted(DateTimeOffset olderThan)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM printed WHERE printed_at < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", Format(olderThan));
        cmd.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Dispose()
    {
        _connection.Dispose();

        // Microsoft.Data.Sqlite faz pooling da conexao nativa por padrao: sem
        // isto, o arquivo continua com handle aberto por um tempo depois do
        // Dispose (percebido nos testes, que apagam o .db logo em seguida).
        SqliteConnection.ClearPool(_connection);
    }
}
