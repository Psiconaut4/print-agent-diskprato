namespace PrintAgent.Printing;

/// <summary>
/// Transporta bytes ESC/POS já formatados (por <c>PrintAgent.Core</c>) até a
/// impressora física. Este projeto nunca formata ESC/POS — só entrega o
/// <c>byte[]</c> pronto pelo caminho que o requisito de convivência com o
/// PDV do cliente permite (§4 do plano).
///
/// Uma implementação nunca decide política de retry/backoff: isso é do
/// <c>PrintAgent.Host</c>. O contrato aqui é "tentei uma vez, aqui está o
/// que aconteceu" — <see cref="PrinterSendResult"/> carrega o suficiente
/// para quem chama decidir se e quando tentar de novo.
/// </summary>
public interface IPrinterTransport
{
    /// <summary>
    /// Envia <paramref name="payload"/> como um job de impressão. Nunca
    /// lança para falhas de impressora/rede/spooler — essas viram um
    /// <see cref="PrinterSendResult"/> malsucedido. Exceções de programação
    /// (argumento nulo, etc.) continuam lançando normalmente.
    /// </summary>
    Task<PrinterSendResult> SendAsync(byte[] payload, CancellationToken ct);
}
