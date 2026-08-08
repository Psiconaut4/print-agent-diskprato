namespace PrintAgent.Printing;

/// <summary>
/// Consulta best-effort de estado da impressora em tempo real. Nem todo
/// <see cref="IPrinterTransport"/> consegue responder isso de verdade —
/// implementar esta interface separada em vez de colocar o método em
/// <see cref="IPrinterTransport"/> deixa isso explícito no tipo, em vez de
/// forçar toda implementação a inventar uma resposta.
/// </summary>
public interface IPrinterStatusQuery
{
    /// <summary>
    /// Nunca lança para falha de consulta (timeout, sem suporte, etc.) —
    /// retorna <see cref="PrinterStatus.Unknown"/> nesses casos.
    /// Reportar "pronta" sem ter certeza é pior do que admitir que não se
    /// sabe (§5.3 do plano).
    /// </summary>
    Task<PrinterStatus> QueryStatusAsync(CancellationToken ct);
}
