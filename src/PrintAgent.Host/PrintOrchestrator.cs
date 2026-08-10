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
/// mandar o ack. Um job impresso ou com retry esgotado já nasce marcado
/// como "não confirmado" (<c>printed/</c>/<c>failed/</c> com <c>acked:
/// false</c>, plano §7.1): quem efetivamente tenta enviar o ack pela rede é
/// um loop separado no <c>Worker</c> (<see cref="AckFlusher"/>, plano §6.5).
/// Isso existe para não travar o processamento de jobs novos esperando o
/// backend responder — <c>JobsApiClient.AckJobAsync</c> já re-tenta
/// indefinidamente para 5xx/rede, e se essa espera acontecesse aqui dentro,
/// um backend fora do ar travaria a impressão de todo mundo.
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

    /// <summary>
    /// Resolve qual impressora usar para um job de destino <paramref name="target"/>
    /// (plano §10) — chamado só depois de decidido que o job vai ser
    /// formatado/enviado, para que uma estação sem impressora configurada vire
    /// retry local em vez de uma exceção não tratada.
    /// </summary>
    public delegate (PrinterProfile Profile, IPrinterTransport Transport) ResolvePrinter(PrintJobTarget target);

    /// <summary>Job recém-chegado (stream ou <c>jobs/pending</c>). Grava antes de tentar imprimir (plano §7.1).</summary>
    public async Task<PrintOutcome> HandleNewJobAsync(PrintJob job, ResolvePrinter resolvePrinter, CancellationToken ct)
    {
        if (_jobStore.IsAlreadyHandled(job.JobId))
        {
            return PrintOutcome.AlreadyHandled;
        }

        _jobStore.RecordReceived(job.JobId, JsonSerializer.Serialize(job, JsonOptions), _timeProvider.GetUtcNow());
        return await AttemptAsync(job, resolvePrinter, attemptNumber: 1, ct).ConfigureAwait(false);
    }

    /// <summary>Reprocessa um job cujo <c>nextAttemptAt</c> já chegou (plano §6.5, loop de retry local).</summary>
    public async Task<PrintOutcome> RetryAsync(PendingJobRecord queued, ResolvePrinter resolvePrinter, CancellationToken ct)
    {
        if (_jobStore.IsAlreadyHandled(queued.JobId))
        {
            _jobStore.RemoveFromQueue(queued.JobId);
            return PrintOutcome.AlreadyHandled;
        }

        var job = JsonSerializer.Deserialize<PrintJob>(queued.PayloadJson, JsonOptions);
        if (job is null)
        {
            // Payload local corrompido: nao ha como reformatar. Desiste sem
            // consumir mais tentativas do schedule normal.
            _jobStore.RecordFailed(queued.JobId, queued.Attempts + 1, PrinterErrorCode.Format_error.ToString(), "payload local corrompido");
            return PrintOutcome.Failed;
        }

        return await AttemptAsync(job, resolvePrinter, attemptNumber: queued.Attempts + 1, ct).ConfigureAwait(false);
    }

    private async Task<PrintOutcome> AttemptAsync(
        PrintJob job, ResolvePrinter resolvePrinter, int attemptNumber, CancellationToken ct)
    {
        PrinterSendResult result;
        try
        {
            // Resolução de impressora por estação (plano §10) entra no mesmo
            // try da formatação/envio: um target sem impressora configurada
            // vira retry local (Not_configured), nunca uma exceção que perde
            // o job ou derruba o Worker inteiro.
            var (profile, transport) = resolvePrinter(job.Target);
            var bytes = _formatter.Format(job, profile);
            result = await transport.SendAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Erro de formatacao (dado do pedido inesperado, bug pontual) ou
            // de resolucao/criacao do transporte (config incompleta): nao
            // adianta re-tentar do mesmo jeito, mas ainda conta como
            // tentativa igual a uma falha normal do transporte.
            result = PrinterSendResult.Fail(PrinterErrorCode.Format_error, isRetryable: false, ex.Message);
        }

        if (result.Success)
        {
            var now = _timeProvider.GetUtcNow();
            _jobStore.RecordPrinted(job.JobId, now, attemptNumber);
            return PrintOutcome.Printed;
        }

        if (attemptNumber >= LocalPrintRetryPolicy.MaxAttempts)
        {
            _jobStore.RecordFailed(job.JobId, attemptNumber, (result.ErrorCode ?? PrinterErrorCode.Unknown).ToString(), result.Detail);
            return PrintOutcome.Failed;
        }

        var delay = LocalPrintRetryPolicy.NextDelay(attemptNumber);
        _jobStore.RecordAttemptFailure(job.JobId, result.Detail ?? result.ErrorCode?.ToString(), _timeProvider.GetUtcNow() + delay);
        return PrintOutcome.Queued;
    }
}
