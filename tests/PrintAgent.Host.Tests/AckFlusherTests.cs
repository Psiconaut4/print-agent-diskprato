using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PrintAgent.Host.Storage;
using PrintAgent.Transport;

namespace PrintAgent.Host.Tests;

public class AckFlusherTests : IDisposable
{
    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private readonly string _queueDir = Path.Combine(Path.GetTempPath(), $"printagent-test-{Guid.NewGuid():N}");
    private readonly JobStore _store;

    public AckFlusherTests()
    {
        _store = new JobStore(_queueDir);
    }

    public void Dispose() => Directory.Delete(_queueDir, recursive: true);

    private AckFlusher FlusherFor(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test") };
        return new AckFlusher(_store, new JobsApiClient(http), NullLogger<AckFlusher>.Instance);
    }

    [Fact]
    public async Task FlushAsync_discards_printed_job_the_backend_no_longer_knows()
    {
        // Regressao (validacao manual de 2026-08-10): AckOutcome.JobNotFound
        // era ignorado em silencio, entao um job orfao em printed/ ficava
        // sendo re-tentado a cada rodada do flusher pra sempre.
        _store.RecordPrinted("orfao", DateTimeOffset.UtcNow, attempts: 1);
        var handler = new StatusHandler(HttpStatusCode.NotFound);

        await FlusherFor(handler).FlushAsync(CancellationToken.None);

        Assert.Empty(_store.GetUnacknowledgedPrinted());

        // Segunda rodada nao pode nem chegar a fazer uma requisicao: o job
        // saiu da fila local de vez.
        await FlusherFor(handler).FlushAsync(CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FlushAsync_discards_failed_job_the_backend_no_longer_knows()
    {
        _store.RecordFailed("orfao", attempts: 5, errorCode: "out_of_paper", errorMessage: "sem papel");

        await FlusherFor(new StatusHandler(HttpStatusCode.NotFound)).FlushAsync(CancellationToken.None);

        Assert.Empty(_store.GetUnacknowledgedFailed());
    }

    [Fact]
    public async Task FlushAsync_marks_acked_on_success()
    {
        _store.RecordPrinted("job1", DateTimeOffset.UtcNow, attempts: 1);

        await FlusherFor(new StatusHandler(HttpStatusCode.OK)).FlushAsync(CancellationToken.None);

        Assert.Empty(_store.GetUnacknowledgedPrinted());
        Assert.True(_store.IsAlreadyHandled("job1")); // continua em printed/, so que acked
    }
}
