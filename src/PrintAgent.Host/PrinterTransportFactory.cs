using PrintAgent.Host.Config;
using PrintAgent.Printing;

namespace PrintAgent.Host;

/// <summary>Resolve o <see cref="IPrinterTransport"/> configurado em <c>agent.json</c> (plano §4/§7.3).</summary>
public static class PrinterTransportFactory
{
    public static IPrinterTransport Create(PrinterConfig config) => config.Transport switch
    {
        PrinterTransportKind.Spooler => new SpoolerPrinterTransport(
            config.SpoolerName ?? throw new InvalidOperationException(
                "printer.transport=spooler exige printer.spoolerName configurado.")),

        PrinterTransportKind.Network => new NetworkPrinterTransport(
            config.Host ?? throw new InvalidOperationException(
                "printer.transport=network exige printer.host configurado."),
            config.Port),

        _ => throw new InvalidOperationException($"Transport de impressora desconhecido: {config.Transport}."),
    };
}
