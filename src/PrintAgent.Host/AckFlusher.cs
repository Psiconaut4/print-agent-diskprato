using Microsoft.Extensions.Logging;
using PrintAgent.Contracts;
using PrintAgent.Host.Storage;
using PrintAgent.Transport;

namespace PrintAgent.Host;

/// <summary>
/// Drena <c>printed/</c> e <c>failed/</c> ainda não confirmados pela rede
/// (plano §6.5). Roda separado do caminho de impressão para que um backend
/// fora do ar nunca atrase a impressão de um pedido novo — só atrasa a
/// confirmação dele, que o servidor já sabe tolerar (o job continua em
/// <c>jobs/pending</c> até o ack chegar).
/// </summary>
public sealed class AckFlusher(JobStore jobStore, JobsApiClient jobsApi, ILogger<AckFlusher> logger)
{
    private static readonly TimeSpan DefaultPerAckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <see cref="JobsApiClient.AckJobAsync"/> já re-tenta indefinidamente
    /// para 5xx/rede (com backoff). Sem um limite por tentativa aqui, um
    /// backend fora do ar travaria esta rodada de flush no primeiro ack para
    /// sempre — cada ack recebe um teto de <paramref name="perAckTimeout"/>;
    /// se estourar, fica pra próxima rodada em vez de travar as demais.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct, TimeSpan? perAckTimeout = null)
    {
        var timeout = perAckTimeout ?? DefaultPerAckTimeout;

        foreach (var printed in jobStore.GetUnacknowledgedPrinted())
        {
            var ack = new AckRequest { Status = AckRequestStatus.Printed, Attempts = printed.Attempts, PrintedAt = printed.PrintedAt };
            if (!await SendAsync(printed.JobId, ack, timeout, ct).ConfigureAwait(false))
            {
                return;
            }
        }

        foreach (var failed in jobStore.GetUnacknowledgedFailed())
        {
            var ack = new AckRequest
            {
                Status = AckRequestStatus.Failed,
                Attempts = failed.Attempts,
                ErrorCode = ParseErrorCode(failed.ErrorCode),
                ErrorMessage = failed.ErrorMessage,
            };
            if (!await SendAsync(failed.JobId, ack, timeout, ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <returns><c>false</c> quando o token deixou de ser válido — sinal para parar a rodada inteira.</returns>
    private async Task<bool> SendAsync(string jobId, AckRequest ack, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(timeout);

        try
        {
            var outcome = await jobsApi.AckJobAsync(jobId, ack, attemptCts.Token).ConfigureAwait(false);
            if (outcome == AckOutcome.Acknowledged)
            {
                jobStore.MarkAcked(jobId);
            }

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Estourou o teto desta tentativa (backend ainda inacessivel ou
            // lento): fica pendente, tenta de novo na proxima rodada.
            jobStore.RecordAckAttemptFailure(jobId, "timeout");
            return true;
        }
        catch (PrintAgentUnauthorizedException)
        {
            // Token invalido: o Worker ja vai reagir a isso no nivel do
            // stream (limpar token, parar, pedir novo pareamento). Aqui so
            // para de tentar mandar mais acks nesta rodada.
            logger.LogWarning("Ack de {JobId} nao pode ser enviado: token invalido.", jobId);
            return false;
        }
        catch (PrintAgentVersionUnsupportedException)
        {
            logger.LogWarning("Ack de {JobId} nao pode ser enviado: versao do agente nao suportada.", jobId);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Erro inesperado (ex.: 400 de um job orfao que o backend nao
            // reconhece mais) nao pode derrubar o resto da rodada — os
            // demais acks pendentes sao independentes deste job.
            logger.LogWarning(ex, "Ack de {JobId} falhou de forma inesperada; tentando de novo na proxima rodada.", jobId);
            jobStore.RecordAckAttemptFailure(jobId, "unexpected-error");
            return true;
        }
    }

    private static PrinterErrorCode? ParseErrorCode(string? errorCode) =>
        errorCode is not null && Enum.TryParse<PrinterErrorCode>(errorCode, out var parsed) ? parsed : null;
}
