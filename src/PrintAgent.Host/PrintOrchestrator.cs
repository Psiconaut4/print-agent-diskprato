using System.Text.Json;
using PrintAgent.Contracts;
using PrintAgent.Core;
using PrintAgent.Core.Retry;
using PrintAgent.Host.Storage;
using PrintAgent.Printing;

namespace PrintAgent.Host;

/// <summary>
/// Decide o que fazer com um <see cref="PrintJob"/>: dedup, formatar
/// (<see cref="EscPosFormatter"/>), enviar (<see cref="IPrinterTransport"/>),
/// e persistir o resultado (<see cref="JobStore"/>).
///
/// Deliberadamente não fala com a API do backend diretamente — nem para
/// mandar o ack. Um ack é sempre gravado primeiro em <c>pending_acks</c>
/// (síncrono, sem rede) e só isso: quem efetivamente tenta enviá-lo pela
/// rede é um loop separado no <c>Worker</c> (plano §6.5: "guardar o ack na
/// fila local e reenviar"). Isso existe para não travar o processamento de
/// jobs novos esperando o backend responder — <c>JobsApiClient.AckJobAsync</c>
/// já re-tenta indefinidamente para 5xx/rede, e se essa espera acontecesse
/// aqui dentro, um backend fora do ar travaria a impressão de todo mundo.
/// </summary>
public sealed class PrintOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly JobStore _jobStore;
    private readonly EscPosFormatter _formatter;
    private readonly TimeProvider _timeProvider;

    public PrintOrchestrator(JobStore jobStore, EscPosFormatter formatter, TimeProvider? timeProvider = null)
    {
        _jobStore = jobStore;
        _formatter = formatter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Job recém-chegado (stream ou <c>jobs/pending</c>). Grava antes de tentar imprimir (plano §7.1).</summary>
    public async Task<PrintOutcome> HandleNewJobAsync(
        PrintJob job, PrinterProfile profile, IPrinterTransport transport, CancellationToken ct)
    {
        if (_jobStore.IsAlreadyPrinted(job.JobId))
        {
            return PrintOutcome.AlreadyHandled;
        }

        _jobStore.RecordReceived(job.JobId, JsonSerializer.Serialize(job, JsonOptions), _timeProvider.GetUtcNow());
        return await AttemptAsync(job, profile, transport, attemptNumber: 1, ct).ConfigureAwait(false);
    }

    /// <summary>Reprocessa um job cujo <c>next_attempt_at</c> já chegou (plano §6.5, loop de retry local).</summary>
    public async Task<PrintOutcome> RetryAsync(
        QueuedJob queued, PrinterProfile profile, IPrinterTransport transport, CancellationToken ct)
    {
        if (_jobStore.IsAlreadyPrinted(queued.JobId))
        {
            _jobStore.RemoveFromQueue(queued.JobId);
            return PrintOutcome.AlreadyHandled;
        }

        var job = JsonSerializer.Deserialize<PrintJob>(queued.PayloadJson, JsonOptions);
        if (job is null)
        {
            // Payload local corrompido: nao ha como reformatar. Desiste sem
            // consumir mais tentativas do schedule normal.
            _jobStore.RemoveFromQueue(queued.JobId);
            EnqueueAck(queued.JobId, AckRequestStatus.Failed, queued.Attempts + 1, null, PrinterErrorCode.Format_error, "payload local corrompido");
            return PrintOutcome.Failed;
        }

        return await AttemptAsync(job, profile, transport, attemptNumber: queued.Attempts + 1, ct).ConfigureAwait(false);
    }

    private async Task<PrintOutcome> AttemptAsync(
        PrintJob job, PrinterProfile profile, IPrinterTransport transport, int attemptNumber, CancellationToken ct)
    {
        PrinterSendResult result;
        try
        {
            var bytes = _formatter.Format(job, profile);
            result = await transport.SendAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Erro de formatacao (dado do pedido inesperado, bug pontual):
            // nao adianta re-tentar do mesmo jeito, mas ainda conta como
            // tentativa igual a uma falha normal do transporte.
            result = PrinterSendResult.Fail(PrinterErrorCode.Format_error, isRetryable: false, ex.Message);
        }

        if (result.Success)
        {
            var now = _timeProvider.GetUtcNow();
            _jobStore.RecordPrinted(job.JobId, now);
            EnqueueAck(job.JobId, AckRequestStatus.Printed, attemptNumber, now, null, null);
            return PrintOutcome.Printed;
        }

        if (attemptNumber >= LocalPrintRetryPolicy.MaxAttempts)
        {
            _jobStore.RemoveFromQueue(job.JobId);
            EnqueueAck(job.JobId, AckRequestStatus.Failed, attemptNumber, null, result.ErrorCode ?? PrinterErrorCode.Unknown, result.Detail);
            return PrintOutcome.Failed;
        }

        var delay = LocalPrintRetryPolicy.NextDelay(attemptNumber);
        _jobStore.RecordAttemptFailure(job.JobId, result.Detail ?? result.ErrorCode?.ToString(), _timeProvider.GetUtcNow() + delay);
        return PrintOutcome.Queued;
    }

    private void EnqueueAck(
        string jobId, AckRequestStatus status, int attempts, DateTimeOffset? printedAt, PrinterErrorCode? errorCode, string? errorMessage)
    {
        var ack = new AckRequest
        {
            Status = status,
            Attempts = attempts,
            PrintedAt = printedAt,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };
        _jobStore.EnqueuePendingAck(jobId, JsonSerializer.Serialize(ack, JsonOptions));
    }
}
