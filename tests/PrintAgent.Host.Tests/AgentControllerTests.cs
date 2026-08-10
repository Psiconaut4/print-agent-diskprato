using PrintAgent.Contracts;
using PrintAgent.Host.Config;
using PrintAgent.Host.Security;
using PrintAgent.Host.Storage;
using PrintAgent.Transport;

namespace PrintAgent.Host.Tests;

/// <summary>Roteamento de impressora por estação e upsert de config por estação (plano §10).</summary>
public class AgentControllerTests : IDisposable
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), $"printagent-test-{Guid.NewGuid():N}");
    private readonly AgentController _controller;

    public AgentControllerTests()
    {
        var configStore = new AgentConfigStore(Path.Combine(_rootDir, "agent.json"));
        var tokenStore = new DeviceTokenStore(Path.Combine(_rootDir, "device.dat"));
        var pairingApi = new PairingApiClient(new HttpClient { BaseAddress = new Uri("https://example.invalid") });
        var jobStore = new JobStore(Path.Combine(_rootDir, "queue"));
        _controller = new AgentController(configStore, tokenStore, pairingApi, jobStore);
    }

    public void Dispose() => Directory.Delete(_rootDir, recursive: true);

    [Fact]
    public void ResolvePrinter_with_empty_config_returns_an_unconfigured_default_instead_of_throwing()
    {
        // Nenhuma impressora cadastrada ainda (instalação nova): nunca lança,
        // deixa o chamador (PrintOrchestrator) tratar como "não configurado"
        // e agendar retry (plano §10 — nunca descarta um job).
        var printer = _controller.ResolvePrinter(PrintJobTarget.Kitchen);

        Assert.Null(printer.SpoolerName);
    }

    [Fact]
    public void UpdatePrinterConfig_with_null_station_upserts_the_default_entry()
    {
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = null, SpoolerName = "Balcao" });
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = null, SpoolerName = "Balcao (renomeada)" });

        var printer = Assert.Single(_controller.Config.Printers);
        Assert.Equal("Balcao (renomeada)", printer.SpoolerName);
    }

    [Fact]
    public void UpdatePrinterConfig_with_different_stations_keeps_separate_entries()
    {
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = null, SpoolerName = "Balcao" });
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = PrintJobTarget.Kitchen, SpoolerName = "Cozinha" });

        Assert.Equal(2, _controller.Config.Printers.Count);
    }

    [Fact]
    public void ResolvePrinter_prefers_the_station_specific_entry_over_the_default()
    {
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = null, SpoolerName = "Balcao" });
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = PrintJobTarget.Kitchen, SpoolerName = "Cozinha" });
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = PrintJobTarget.Bar, SpoolerName = "Bar" });

        Assert.Equal("Cozinha", _controller.ResolvePrinter(PrintJobTarget.Kitchen).SpoolerName);
        Assert.Equal("Bar", _controller.ResolvePrinter(PrintJobTarget.Bar).SpoolerName);

        // target sem impressora dedicada (Counter/Customer) cai na "padrão" (Station == null).
        Assert.Equal("Balcao", _controller.ResolvePrinter(PrintJobTarget.Counter).SpoolerName);
        Assert.Equal("Balcao", _controller.ResolvePrinter(PrintJobTarget.Customer).SpoolerName);
    }

    [Fact]
    public void ResolvePrinter_without_a_default_entry_falls_back_to_the_first_configured_printer()
    {
        // Só estações dedicadas configuradas, nenhuma "padrão" (Station == null):
        // um target sem impressora própria não pode ficar sem imprimir.
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = PrintJobTarget.Kitchen, SpoolerName = "Cozinha" });

        Assert.Equal("Cozinha", _controller.ResolvePrinter(PrintJobTarget.Counter).SpoolerName);
    }

    [Fact]
    public void ResolveDefaultPrinter_used_by_the_pipe_matches_the_null_station_entry()
    {
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = PrintJobTarget.Kitchen, SpoolerName = "Cozinha" });
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = null, SpoolerName = "Balcao" });

        Assert.Equal("Balcao", _controller.ResolveDefaultPrinter().SpoolerName);
    }

    [Fact]
    public void RemovePrinterConfig_removes_only_the_matching_station()
    {
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = null, SpoolerName = "Balcao" });
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = PrintJobTarget.Kitchen, SpoolerName = "Cozinha" });

        _controller.RemovePrinterConfig(PrintJobTarget.Kitchen);

        var printer = Assert.Single(_controller.Config.Printers);
        Assert.Null(printer.Station);
        Assert.Equal("Balcao", printer.SpoolerName);
    }

    [Fact]
    public void RemovePrinterConfig_of_a_station_that_does_not_exist_is_a_no_op()
    {
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = null, SpoolerName = "Balcao" });

        _controller.RemovePrinterConfig(PrintJobTarget.Bar);

        Assert.Single(_controller.Config.Printers);
    }
}
