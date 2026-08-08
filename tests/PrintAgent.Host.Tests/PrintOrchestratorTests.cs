using Microsoft.Extensions.Time.Testing;
using PrintAgent.Contracts;
using PrintAgent.Core;
using PrintAgent.Core.Retry;
using PrintAgent.Host.Storage;
using PrintAgent.Printing;

namespace PrintAgent.Host.Tests;

public class PrintOrchestratorTests : IDisposable
{
    private readonly string _queueDir = Path.Combine(Path.GetTempPath(), $"printagent-test-{Guid.NewGuid():N}");
    private readonly JobStore _store;
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.UtcNow);
    private readonly PrintOrchestrator _orchestrator;

    public PrintOrchestratorTests()
    {
        _store = new JobStore(_queueDir);
        _orchestrator = new PrintOrchestrator(_store, new EscPosFormatter(), _timeProvider);
    }

    public void Dispose() => Directory.Delete(_queueDir, recursive: true);

    private static PrintJob BuildJob(string jobId = "job1") => new()
    {
        JobId = jobId,
        OrderId = "order1",
        RestaurantId = "rest1",
        Kind = PrintJobKind.Order,
        Target = PrintJobTarget.Kitchen,
        Copies = 1,
        IssuedAt = DateTimeOffset.UtcNow,
        Restaurant = new Restaurant2 { Name = "Restaurante Teste" },
        Order = new PrintOrder
        {
            Number = "1",
            CreatedAt = DateTimeOffset.UtcNow,
            FulfillmentType = PrintOrderFulfillmentType.Pickup,
            Customer = new Customer { Name = "Cliente", Phone = "0000" },
            Payment = new PrintPayment { Method = PrintPaymentMethod.Cash, Status = PrintPaymentStatus.Paid, Label = "Dinheiro" },
            Items = [new PrintItem { Quantity = 1, Name = "Item", UnitPriceCents = 100, TotalPriceCents = 100 }],
            SubtotalCents = 100,
            DeliveryFeeCents = 0,
            TotalCents = 100,
            Currency = PrintOrderCurrency.BRL,
        },
    };

    private sealed class FakeTransport(Func<PrinterSendResult> resultFactory) : IPrinterTransport
    {
        public int CallCount { get; private set; }

        public Task<PrinterSendResult> SendAsync(byte[] payload, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(resultFactory());
        }
    }

    [Fact]
    public async Task HandleNewJobAsync_success_marks_printed_and_enqueues_ack()
    {
        var transport = new FakeTransport(PrinterSendResult.Ok);

        var outcome = await _orchestrator.HandleNewJobAsync(BuildJob(), PrinterProfile.Default, transport, CancellationToken.None);

        Assert.Equal(PrintOutcome.Printed, outcome);
        Assert.True(_store.IsAlreadyHandled("job1"));
        var printed = Assert.Single(_store.GetUnacknowledgedPrinted());
        Assert.Equal("job1", printed.JobId);
        Assert.Equal(1, printed.Attempts);
    }

    [Fact]
    public async Task HandleNewJobAsync_already_printed_does_not_call_transport_again()
    {
        var transport = new FakeTransport(PrinterSendResult.Ok);
        await _orchestrator.HandleNewJobAsync(BuildJob(), PrinterProfile.Default, transport, CancellationToken.None);

        var outcome = await _orchestrator.HandleNewJobAsync(BuildJob(), PrinterProfile.Default, transport, CancellationToken.None);

        Assert.Equal(PrintOutcome.AlreadyHandled, outcome);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task HandleNewJobAsync_retryable_failure_queues_for_retry()
    {
        var transport = new FakeTransport(() => PrinterSendResult.Fail(PrinterErrorCode.Printer_busy, isRetryable: true));

        var outcome = await _orchestrator.HandleNewJobAsync(BuildJob(), PrinterProfile.Default, transport, CancellationToken.None);

        Assert.Equal(PrintOutcome.Queued, outcome);
        Assert.False(_store.IsAlreadyHandled("job1"));
        Assert.Empty(_store.GetDueJobs(_timeProvider.GetUtcNow()));
        Assert.Single(_store.GetDueJobs(_timeProvider.GetUtcNow() + LocalPrintRetryPolicy.NextDelay(1)));
    }

    [Fact]
    public async Task RetryAsync_exhausting_attempts_marks_failed_and_enqueues_failed_ack()
    {
        var transport = new FakeTransport(() => PrinterSendResult.Fail(PrinterErrorCode.Out_of_paper, isRetryable: false));

        var outcome = await _orchestrator.HandleNewJobAsync(BuildJob(), PrinterProfile.Default, transport, CancellationToken.None);
        Assert.Equal(PrintOutcome.Queued, outcome);

        for (var attempt = 2; attempt <= LocalPrintRetryPolicy.MaxAttempts; attempt++)
        {
            var due = Assert.Single(_store.GetDueJobs(DateTimeOffset.UtcNow.AddDays(1)));
            outcome = await _orchestrator.RetryAsync(due, PrinterProfile.Default, transport, CancellationToken.None);
        }

        Assert.Equal(PrintOutcome.Failed, outcome);
        Assert.Equal(LocalPrintRetryPolicy.MaxAttempts, transport.CallCount);
        Assert.Empty(_store.GetDueJobs(DateTimeOffset.UtcNow.AddDays(1)));

        var failed = Assert.Single(_store.GetUnacknowledgedFailed());
        Assert.Equal("job1", failed.JobId);
        Assert.Equal("Out_of_paper", failed.ErrorCode);
    }
}
