using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrintAgent.Contracts;
using PrintAgent.Host.Storage;
using PrintAgent.Transport;

namespace PrintAgent.Host;

/// <summary>
/// Drena <c>pending_acks</c> pela rede (plano §6.5). Roda separado do
/// caminho de impressão para que um backend fora do ar nunca atrase a
/// impressão de um pedido novo — só atrasa a confirmação dele, que o
/// servidor já sabe tolerar (o job continua em <c>jobs/pending</c> até o ack
/// chegar).
/// </summary>
public sealed class AckFlusher(JobStore jobStore, JobsApiClient jobsApi, ILogger<AckFlusher> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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

        foreach (var pending in jobStore.GetPendingAcks())
        {
            ct.ThrowIfCancellationRequested();

            var ack = JsonSerializer.Deserialize<AckRequest>(pending.BodyJson, JsonOptions);
            if (ack is null)
            {
                // corpo local corrompido: nao ha o que reenviar, descarta.
                jobStore.RemovePendingAck(pending.JobId);
                continue;
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(timeout);

            try
            {
                var outcome = await jobsApi.AckJobAsync(pending.JobId, ack, attemptCts.Token).ConfigureAwait(false);
                jobStore.RemovePendingAck(pending.JobId);
                if (outcome == AckOutcome.Acknowledged)
                {
                    jobStore.MarkAcked(pending.JobId);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Estourou o teto desta tentativa (backend ainda inacessivel
                // ou lento): fica pendente, tenta de novo na proxima rodada.
                jobStore.IncrementAckAttempt(pending.JobId);
            }
            catch (PrintAgentUnauthorizedException)
            {
                // Token invalido: o Worker ja vai reagir a isso no nivel do
                // stream (limpar token, parar, pedir novo pareamento). Aqui
                // so para de tentar mandar mais acks nesta rodada.
                logger.LogWarning("Ack de {JobId} nao pode ser enviado: token invalido.", pending.JobId);
                return;
            }
            catch (PrintAgentVersionUnsupportedException)
            {
                logger.LogWarning("Ack de {JobId} nao pode ser enviado: versao do agente nao suportada.", pending.JobId);
                return;
            }
        }
    }
}
