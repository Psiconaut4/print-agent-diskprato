using PrintAgent.Core;

namespace PrintAgent.Host.Config;

/// <summary>Qual <c>IPrinterTransport</c> usar (plano §4). Escape hatch USB direta (§4.3) fica fora do v1.</summary>
public enum PrinterTransportKind
{
    Spooler,
    Network,
}

/// <summary>
/// Configuração local da impressora (plano §7.3, <c>agent.json</c>). Não faz
/// parte do contrato OpenAPI — é config exclusiva da máquina do balcão.
/// </summary>
public sealed class PrinterConfig
{
    public PrinterTransportKind Transport { get; set; } = PrinterTransportKind.Spooler;

    /// <summary>Nome da fila do Windows, quando <see cref="Transport"/> é <see cref="PrinterTransportKind.Spooler"/>.</summary>
    public string? SpoolerName { get; set; }

    /// <summary>Host/IP, quando <see cref="Transport"/> é <see cref="PrinterTransportKind.Network"/>.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 9100;

    public int PaperWidthMm { get; set; } = 80;

    public int CodePage { get; set; } = 850;

    public int EscTIndex { get; set; } = 2;

    public bool StripAccents { get; set; }

    public int Copies { get; set; } = 1;

    public PrinterProfile ToProfile() => new(PaperWidthMm, CodePage, EscTIndex, StripAccents, Copies);
}
