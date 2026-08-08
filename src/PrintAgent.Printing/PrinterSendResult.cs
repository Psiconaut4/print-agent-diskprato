using PrintAgent.Contracts;

namespace PrintAgent.Printing;

/// <summary>
/// Resultado de um envio de bytes a uma impressora física por um
/// <see cref="IPrinterTransport"/>.
///
/// Este tipo só reporta o que aconteceu; ele nunca decide política de
/// retry/backoff — isso é responsabilidade de quem chama (PrintAgent.Host).
/// O campo <see cref="IsRetryable"/> é a única opinião que o transporte dá:
/// "vale a pena tentar de novo" versus "não vai adiantar sem intervenção".
/// </summary>
public sealed class PrinterSendResult
{
    /// <summary>True quando os bytes saíram com sucesso pelo transporte.</summary>
    public bool Success { get; }

    /// <summary>
    /// Código de erro do vocabulário fechado do contrato. Nulo quando
    /// <see cref="Success"/> é true.
    /// </summary>
    public PrinterErrorCode? ErrorCode { get; }

    /// <summary>
    /// True quando o erro é transitório e provavelmente não é culpa do
    /// DiskPrato — o caso canônico é <see cref="PrinterErrorCode.Printer_busy"/>,
    /// que normalmente significa que outro PDV está usando a impressora
    /// naquele instante. False para erros que não vão se resolver sozinhos
    /// (sem papel, tampa aberta, impressora não configurada).
    /// </summary>
    public bool IsRetryable { get; }

    /// <summary>Detalhe técnico livre (mensagem de exceção, código Win32, etc.), só para log/diagnóstico.</summary>
    public string? Detail { get; }

    private PrinterSendResult(bool success, PrinterErrorCode? errorCode, bool isRetryable, string? detail)
    {
        Success = success;
        ErrorCode = errorCode;
        IsRetryable = isRetryable;
        Detail = detail;
    }

    public static PrinterSendResult Ok() => new(true, null, false, null);

    public static PrinterSendResult Fail(PrinterErrorCode errorCode, bool isRetryable, string? detail = null) =>
        new(false, errorCode, isRetryable, detail);
}
