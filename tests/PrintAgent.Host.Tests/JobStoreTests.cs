using PrintAgent.Host.Storage;

namespace PrintAgent.Host.Tests;

public class JobStoreTests : IDisposable
{
    private readonly string _queueDir = Path.Combine(Path.GetTempPath(), $"printagent-test-{Guid.NewGuid():N}");
    private readonly JobStore _store;

    public JobStoreTests()
    {
        _store = new JobStore(_queueDir);
    }

    public void Dispose() => Directory.Delete(_queueDir, recursive: true);

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
    public void IsAlreadyHandled_reflects_printed_folder()
    {
        Assert.False(_store.IsAlreadyHandled("job1"));

        _store.RecordReceived("job1", "{}", DateTimeOffset.UtcNow);
        _store.RecordPrinted("job1", DateTimeOffset.UtcNow, attempts: 1);

        Assert.True(_store.IsAlreadyHandled("job1"));
    }

    [Fact]
    public void IsAlreadyHandled_reflects_failed_folder()
    {
        Assert.False(_store.IsAlreadyHandled("job1"));

        _store.RecordReceived("job1", "{}", DateTimeOffset.UtcNow);
        _store.RecordFailed("job1", attempts: 5, errorCode: "out_of_paper", errorMessage: "sem papel");

        Assert.True(_store.IsAlreadyHandled("job1"));
    }

    [Fact]
    public void RecordPrinted_removes_job_from_retry_queue()
    {
        var now = DateTimeOffset.UtcNow;
        _store.RecordReceived("job1", "{}", now);
        _store.RecordPrinted("job1", now, attempts: 1);

        Assert.Empty(_store.GetDueJobs(now.AddDays(1)));
        Assert.Equal(0, _store.GetQueueLength());
    }

    [Fact]
    public void RecordFailed_removes_job_from_retry_queue()
    {
        var now = DateTimeOffset.UtcNow;
        _store.RecordReceived("job1", "{}", now);
        _store.RecordFailed("job1", attempts: 5, errorCode: "out_of_paper", errorMessage: "sem papel");

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
    public void Unacknowledged_printed_round_trip()
    {
        _store.RecordReceived("job1", "{}", DateTimeOffset.UtcNow);
        _store.RecordPrinted("job1", DateTimeOffset.UtcNow, attempts: 2);

        var printed = Assert.Single(_store.GetUnacknowledgedPrinted());
        Assert.Equal("job1", printed.JobId);
        Assert.Equal(2, printed.Attempts);
        Assert.False(printed.Acked);

        _store.RecordAckAttemptFailure("job1", "timeout");
        printed = Assert.Single(_store.GetUnacknowledgedPrinted());
        Assert.Equal("timeout", printed.LastAckError);

        _store.MarkAcked("job1");
        Assert.Empty(_store.GetUnacknowledgedPrinted());
    }

    [Fact]
    public void Unacknowledged_failed_round_trip()
    {
        _store.RecordReceived("job1", "{}", DateTimeOffset.UtcNow);
        _store.RecordFailed("job1", attempts: 5, errorCode: "out_of_paper", errorMessage: "sem papel");

        var failed = Assert.Single(_store.GetUnacknowledgedFailed());
        Assert.Equal("job1", failed.JobId);
        Assert.Equal("out_of_paper", failed.ErrorCode);
        Assert.False(failed.Acked);

        _store.MarkAcked("job1");
        Assert.Empty(_store.GetUnacknowledgedFailed());
    }

    [Fact]
    public void CleanupOldPrinted_removes_only_records_older_than_cutoff()
    {
        var now = DateTimeOffset.UtcNow;
        _store.RecordReceived("old", "{}", now);
        _store.RecordPrinted("old", now.AddDays(-10), attempts: 1);
        _store.RecordReceived("recent", "{}", now);
        _store.RecordPrinted("recent", now.AddDays(-1), attempts: 1);

        _store.CleanupOldPrinted(now.AddDays(-7));

        Assert.False(_store.IsAlreadyHandled("old"));
        Assert.True(_store.IsAlreadyHandled("recent"));
    }

    [Fact]
    public void CleanupOldPrinted_also_removes_old_failed_records()
    {
        var now = DateTimeOffset.UtcNow;
        _store.RecordReceived("old", "{}", now);
        _store.RecordFailed("old", attempts: 5, errorCode: "out_of_paper", errorMessage: null);

        // simula um registro antigo: reescreve o arquivo com failedAt no passado.
        var failedPath = Path.Combine(_queueDir, "failed", "old.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(failedPath))!;
        node["failedAt"] = now.AddDays(-10).ToString("O");
        File.WriteAllText(failedPath, node.ToJsonString());

        _store.CleanupOldPrinted(now.AddDays(-7));

        Assert.False(_store.IsAlreadyHandled("old"));
    }
}
