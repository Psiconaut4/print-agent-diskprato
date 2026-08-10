using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintAgent.Host.Storage;

/// <summary>
/// Fila local em arquivo (plano §7.1) —
/// <c>%ProgramData%\DiskPrato\PrintAgent\queue\</c>, um <c>.json</c> por job em
/// <c>pending/</c>, <c>printed/</c> ou <c>failed/</c>. Sobrevive a reboot da
/// máquina da loja, o que o replay do SSE (TTL de 5 min) não cobre.
///
/// Estado é a pasta em que o arquivo está, não um campo dentro dele — a
/// transição entre estados é gravar no destino e só depois apagar da origem,
/// nessa ordem, para nunca existir uma janela em que o job não existe em
/// lugar nenhum. Escrita sempre atômica (arquivo temporário + <see cref="File.Move"/>
/// no mesmo volume).
/// </summary>
public sealed class JobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _pendingDir;
    private readonly string _printedDir;
    private readonly string _failedDir;

    public JobStore(string? queueDirectory = null)
    {
        var root = queueDirectory ?? Path.Combine(Config.AgentConfigStore.DefaultDirectory, "queue");
        _pendingDir = Path.Combine(root, "pending");
        _printedDir = Path.Combine(root, "printed");
        _failedDir = Path.Combine(root, "failed");

        Directory.CreateDirectory(_pendingDir);
        Directory.CreateDirectory(_printedDir);
        Directory.CreateDirectory(_failedDir);
    }

    /// <summary>
    /// Já chegou a um estado terminal (impresso ou falhou em definitivo). O
    /// mesmo job chega pelo stream e por <c>jobs/pending</c> — este é o ponto
    /// de dedup (plano §6.2/§7.1), não é otimização, é correção. Cobre os
    /// dois estados terminais: um job que já falhou definitivamente também
    /// não deve reentrar em <c>pending/</c>.
    /// </summary>
    public bool IsAlreadyHandled(string jobId) => File.Exists(PrintedPath(jobId)) || File.Exists(FailedPath(jobId));

    /// <summary>
    /// Grava o job recebido antes de qualquer outra coisa (plano §7.1: "escrever
    /// em pending/ antes de responder qualquer coisa"). Idempotente — reenvio
    /// do mesmo jobId não duplica nem reseta tentativas já feitas.
    /// </summary>
    public void RecordReceived(string jobId, string payloadJson, DateTimeOffset receivedAt)
    {
        if (File.Exists(PendingPath(jobId)))
        {
            return;
        }

        var record = new PendingJobRecord(jobId, payloadJson, receivedAt, Attempts: 0, NextAttemptAt: receivedAt, LastError: null);
        WriteAtomic(PendingPath(jobId), record);
    }

    public void RemoveFromQueue(string jobId) => TryDelete(PendingPath(jobId));

    /// <summary>Marca como impresso e tira da fila de retry — grava em <c>printed/</c> antes de apagar de <c>pending/</c>.</summary>
    public void RecordPrinted(string jobId, DateTimeOffset printedAt, int attempts)
    {
        var record = new PrintedJobRecord(jobId, printedAt, attempts, Acked: false, LastAckAttemptAt: null, LastAckError: null);
        WriteAtomic(PrintedPath(jobId), record);
        TryDelete(PendingPath(jobId));
    }

    /// <summary>Esgotou o retry local (plano §6.5) — grava em <c>failed/</c> antes de apagar de <c>pending/</c>.</summary>
    public void RecordFailed(string jobId, int attempts, string? errorCode, string? errorMessage)
    {
        var record = new FailedJobRecord(
            jobId, DateTimeOffset.UtcNow, attempts, errorCode, errorMessage, Acked: false, LastAckAttemptAt: null, LastAckError: null);
        WriteAtomic(FailedPath(jobId), record);
        TryDelete(PendingPath(jobId));
    }

    /// <summary>Registra uma tentativa de impressão que falhou e agenda a próxima (plano §6.5).</summary>
    public void RecordAttemptFailure(string jobId, string? error, DateTimeOffset nextAttemptAt)
    {
        var current = ReadPending(jobId);
        if (current is null)
        {
            return;
        }

        var updated = current with { Attempts = current.Attempts + 1, NextAttemptAt = nextAttemptAt, LastError = error };
        WriteAtomic(PendingPath(jobId), updated);
    }

    /// <summary>Jobs cujo próximo horário de tentativa já chegou, mais antigos primeiro (por <c>receivedAt</c>, nunca pela ordem do diretório).</summary>
    public IReadOnlyList<PendingJobRecord> GetDueJobs(DateTimeOffset now) =>
        ReadAllPending()
            .Where(job => job.NextAttemptAt <= now)
            .OrderBy(job => job.ReceivedAt)
            .ToList();

    /// <summary>Tamanho atual da fila de retry — vira <c>StatusReport.queuedJobs</c> (plano §6, best-effort).</summary>
    public int GetQueueLength() => Directory.EnumerateFiles(_pendingDir, "*.json").Count();

    /// <summary>Jobs impressos ainda não confirmados ao backend (plano §6.5, drenado pelo <c>AckFlusher</c>).</summary>
    public IReadOnlyList<PrintedJobRecord> GetUnacknowledgedPrinted() =>
        ReadAll<PrintedJobRecord>(_printedDir).Where(job => !job.Acked).ToList();

    /// <summary>Jobs com retry local esgotado ainda não confirmados ao backend (plano §6.5, drenado pelo <c>AckFlusher</c>).</summary>
    public IReadOnlyList<FailedJobRecord> GetUnacknowledgedFailed() =>
        ReadAll<FailedJobRecord>(_failedDir).Where(job => !job.Acked).ToList();

    public void MarkAcked(string jobId)
    {
        var printedPath = PrintedPath(jobId);
        if (File.Exists(printedPath))
        {
            var printed = Read<PrintedJobRecord>(printedPath);
            if (printed is not null)
            {
                WriteAtomic(printedPath, printed with { Acked = true });
            }

            return;
        }

        var failedPath = FailedPath(jobId);
        var failed = Read<FailedJobRecord>(failedPath);
        if (failed is not null)
        {
            WriteAtomic(failedPath, failed with { Acked = true });
        }
    }

    /// <summary>
    /// Descarta um job terminal da fila local sem confirmar nada (plano §6.6:
    /// 404 no ack → o backend não conhece mais este job, não há o que
    /// confirmar). Sem isto, um job órfão em <c>printed/</c>/<c>failed/</c>
    /// ficaria sendo re-tentado pelo <c>AckFlusher</c> para sempre, já que
    /// <see cref="MarkAcked"/> só é chamado no caminho de sucesso.
    /// </summary>
    public void Discard(string jobId)
    {
        TryDelete(PrintedPath(jobId));
        TryDelete(FailedPath(jobId));
    }

    /// <summary>Estourou o teto de tentativa do <c>AckFlusher</c> para este job — fica pendente pra próxima rodada.</summary>
    public void RecordAckAttemptFailure(string jobId, string? error)
    {
        var now = DateTimeOffset.UtcNow;
        var printedPath = PrintedPath(jobId);
        if (File.Exists(printedPath))
        {
            var printed = Read<PrintedJobRecord>(printedPath);
            if (printed is not null)
            {
                WriteAtomic(printedPath, printed with { LastAckAttemptAt = now, LastAckError = error });
            }

            return;
        }

        var failedPath = FailedPath(jobId);
        var failed = Read<FailedJobRecord>(failedPath);
        if (failed is not null)
        {
            WriteAtomic(failedPath, failed with { LastAckAttemptAt = now, LastAckError = error });
        }
    }

    /// <summary>Limpa <c>printed/</c> e <c>failed/</c> com mais de 7 dias — janela folgada sobre as 24h que <c>jobs/pending</c> cobre (plano §7.1). <c>pending/</c> nunca é limpo por idade.</summary>
    public void CleanupOldPrinted(DateTimeOffset olderThan)
    {
        foreach (var printed in ReadAll<PrintedJobRecord>(_printedDir))
        {
            if (printed.PrintedAt < olderThan)
            {
                TryDelete(PrintedPath(printed.JobId));
            }
        }

        foreach (var failed in ReadAll<FailedJobRecord>(_failedDir))
        {
            if (failed.FailedAt < olderThan)
            {
                TryDelete(FailedPath(failed.JobId));
            }
        }
    }

    private PendingJobRecord? ReadPending(string jobId) => Read<PendingJobRecord>(PendingPath(jobId));

    private IReadOnlyList<PendingJobRecord> ReadAllPending() => ReadAll<PendingJobRecord>(_pendingDir);

    private static IReadOnlyList<T> ReadAll<T>(string directory)
    {
        var result = new List<T>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var record = Read<T>(path);
            if (record is not null)
            {
                result.Add(record);
            }
        }

        return result;
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // arquivo local corrompido: trata como ausente em vez de derrubar o processo.
            return default;
        }
    }

    private static void WriteAtomic<T>(string path, T record)
    {
        var directory = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}.json.tmp-{Guid.NewGuid():N}");
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best-effort: se outro processo segura o arquivo, tenta de novo na proxima rodada.
        }
    }

    private string PendingPath(string jobId) => Path.Combine(_pendingDir, $"{jobId}.json");

    private string PrintedPath(string jobId) => Path.Combine(_printedDir, $"{jobId}.json");

    private string FailedPath(string jobId) => Path.Combine(_failedDir, $"{jobId}.json");
}
