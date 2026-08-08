using PrintAgent.Host.Storage;

namespace PrintAgent.Host.Tests;

public class JobStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"printagent-test-{Guid.NewGuid():N}.db");
    private readonly JobStore _store;

    public JobStoreTests()
    {
        _store = new JobStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
        // SQLite em modo WAL cria -wal/-shm ao lado do arquivo principal.
        File.Delete(_dbPath + "-wal");
        File.Delete(_dbPath + "-shm");
    }

    [Fact]
    public void RecordReceived_is_idempotent()
    {
        var now = DateTimeOffset.UtcNow;
        _store.RecordReceived("job1", "{}", now);
        _store.RecordAttemptFailure("job1", "erro", now.AddMinutes(1));
        _store.RecordReceived("job1", "{}", now); // reenvio do mesmo job: nao deve resetar attempts

        var due = _store.GetDueJobs(now.AddMinutes(2));
        Assert.Single(due);
        Assert.Equal(1, due[0].Attempts);
    }

    [Fact]
    public void IsAlreadyPrinted_reflects_printed_table()
    {
        Assert.False(_store.IsAlreadyPrinted("job1"));

        _store.RecordReceived("job1", "{}", DateTimeOffset.UtcNow);
        _store.RecordPrinted("job1", DateTimeOffset.UtcNow);

        Assert.True(_store.IsAlreadyPrinted("job1"));
    }

    [Fact]
    public void RecordPrinted_removes_job_from_retry_queue()
    {
        var now = DateTimeOffset.UtcNow;
        _store.RecordReceived("job1", "{}", now);
        _store.RecordPrinted("job1", now);

        Assert.Empty(_store.GetDueJobs(now.AddDays(1)));
        Assert.Equal(0, _store.GetQueueLength());
    }

    [Fact]
    public void GetDueJobs_only_returns_jobs_whose_next_attempt_already_arrived()
    {
        var now = DateTimeOffset.UtcNow;
        _store.RecordReceived("job1", "{}", now);
        _store.RecordAttemptFailure("job1", "erro", now.AddMinutes(5));

        Assert.Empty(_store.GetDueJobs(now));
        Assert.Single(_store.GetDueJobs(now.AddMinutes(5)));
    }

    [Fact]
    public void Pending_acks_round_trip()
    {
        _store.EnqueuePendingAck("job1", "{\"status\":\"printed\"}");

        var pending = Assert.Single(_store.GetPendingAcks());
        Assert.Equal("job1", pending.JobId);
        Assert.Equal(0, pending.Attempts);

        _store.IncrementAckAttempt("job1");
        pending = Assert.Single(_store.GetPendingAcks());
        Assert.Equal(1, pending.Attempts);

        _store.RemovePendingAck("job1");
        Assert.Empty(_store.GetPendingAcks());
    }

    [Fact]
    public void CleanupOldPrinted_removes_only_rows_older_than_cutoff()
    {
        var now = DateTimeOffset.UtcNow;
        _store.RecordReceived("old", "{}", now);
        _store.RecordPrinted("old", now.AddDays(-10));
        _store.RecordReceived("recent", "{}", now);
        _store.RecordPrinted("recent", now.AddDays(-1));

        _store.CleanupOldPrinted(now.AddDays(-7));

        Assert.False(_store.IsAlreadyPrinted("old"));
        Assert.True(_store.IsAlreadyPrinted("recent"));
    }
}
