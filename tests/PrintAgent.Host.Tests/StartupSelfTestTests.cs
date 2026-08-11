using PrintAgent.Host.Config;
using PrintAgent.Host.Diagnostics;
using PrintAgent.Host.Security;
using PrintAgent.Host.Storage;
using PrintAgent.Printing;
using PrintAgent.Transport;

namespace PrintAgent.Host.Tests;

/// <summary>
/// Auto-teste da inicialização (plano §8, Fase 8) — diretório temporário real,
/// sem mock de filesystem, como o resto da suíte.
/// </summary>
public class StartupSelfTestTests : IDisposable
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), $"printagent-test-{Guid.NewGuid():N}");
    private readonly AgentConfigStore _configStore;
    private readonly AgentController _controller;
    private readonly StartupSelfTest _selfTest;

    public StartupSelfTestTests()
    {
        _configStore = new AgentConfigStore(Path.Combine(_rootDir, "agent.json"));
        var tokenStore = new DeviceTokenStore(Path.Combine(_rootDir, "device.dat"));
        var pairingApi = new PairingApiClient(new HttpClient { BaseAddress = new Uri("https://example.invalid") });
        var jobStore = new JobStore(Path.Combine(_rootDir, "queue"));
        _controller = new AgentController(_configStore, tokenStore, pairingApi, jobStore);
        _selfTest = new StartupSelfTest(_controller, jobStore) { LogsDirectory = Path.Combine(_rootDir, "logs") };
    }

    public void Dispose() => Directory.Delete(_rootDir, recursive: true);

    private static SelfTestCheck Find(IReadOnlyList<SelfTestCheck> checks, string name) =>
        Assert.Single(checks, check => check.Name == name);

    [Fact]
    public async Task Reports_a_fresh_install_as_unpaired_and_without_printers()
    {
        var checks = await _selfTest.RunAsync(CancellationToken.None);

        Assert.False(Find(checks, "Pareamento").Ok);
        Assert.False(Find(checks, "Impressoras").Ok);
    }

    [Fact]
    public async Task Reports_the_queue_and_log_directories_as_writable()
    {
        var checks = await _selfTest.RunAsync(CancellationToken.None);

        // Existir nao basta: o check grava e apaga um arquivo de sonda, porque
        // e a escrita que revela a ACL restrita do %ProgramData% (plano §7.2).
        Assert.True(Find(checks, "Fila local").Ok);
        Assert.True(Find(checks, "Pasta de log").Ok);
    }

    [Fact]
    public async Task Flags_an_api_base_url_that_is_not_https()
    {
        // O token do dispositivo viaja em todo request (plano §7.2): http:// e
        // util em teste local, mas nunca pode passar despercebido.
        _configStore.Save(new AgentConfig { ApiBaseUrl = "http://api.exemplo.invalid" });
        var controller = new AgentController(
            _configStore,
            new DeviceTokenStore(Path.Combine(_rootDir, "device.dat")),
            new PairingApiClient(new HttpClient { BaseAddress = new Uri("https://example.invalid") }),
            new JobStore(Path.Combine(_rootDir, "queue")));

        var checks = await new StartupSelfTest(controller, new JobStore(Path.Combine(_rootDir, "queue")))
        {
            LogsDirectory = Path.Combine(_rootDir, "logs"),
        }.RunAsync(CancellationToken.None);

        Assert.False(Find(checks, "Endereco da API").Ok);
    }

    [Fact]
    public async Task Flags_an_api_base_url_that_carries_a_path()
    {
        // Os caminhos dos clientes sao root-relative e ja trazem o /api, entao
        // um caminho aqui e descartado em silencio: sem esse aviso, quem
        // escreve ".../api" no agent.json nao ve diferenca nenhuma.
        _configStore.Save(new AgentConfig { ApiBaseUrl = "https://app.exemplo.invalid/api" });
        var controller = new AgentController(
            _configStore,
            new DeviceTokenStore(Path.Combine(_rootDir, "device.dat")),
            new PairingApiClient(new HttpClient { BaseAddress = new Uri("https://example.invalid") }),
            new JobStore(Path.Combine(_rootDir, "queue")));

        var checks = await new StartupSelfTest(controller, new JobStore(Path.Combine(_rootDir, "queue")))
        {
            LogsDirectory = Path.Combine(_rootDir, "logs"),
        }.RunAsync(CancellationToken.None);

        var check = Find(checks, "Endereco da API");
        Assert.False(check.Ok);
        Assert.Contains("https://app.exemplo.invalid", check.Detail);
    }

    [Fact]
    public async Task Reports_a_printer_without_queue_or_host_as_a_failure()
    {
        _controller.UpdatePrinterConfig(new PrinterConfig { Station = null, SpoolerName = "" });

        var checks = await _selfTest.RunAsync(CancellationToken.None);

        var printer = Find(checks, "Impressora (padrao)");
        Assert.False(printer.Ok);
        Assert.Contains("Sem fila/IP", printer.Detail);
    }

    [Fact]
    public async Task Does_not_claim_a_verdict_for_a_network_printer_that_never_answers()
    {
        // Porta fechada em localhost. NetworkPrinterTransport responde Unknown,
        // nao Offline, quando ninguem atende (decisao da Fase 5, fixada em
        // NetworkPrinterTransportStatusTests: nunca afirmar um estado que nao
        // se sabe, plano §5.3). O auto-teste respeita isso e nao inventa uma
        // falha — Ok fica nulo, e a linha sai marcada "?" no log.
        _controller.UpdatePrinterConfig(new PrinterConfig
        {
            Station = null,
            Transport = PrinterTransportKind.Network,
            Host = "127.0.0.1",
            Port = 9101,
        });

        var checks = await _selfTest.RunAsync(CancellationToken.None);

        var printer = Find(checks, "Impressora (padrao)");
        Assert.Null(printer.Ok);
        Assert.Contains("nao reportado", printer.Detail);
    }

    [Fact]
    public void Maps_every_printer_state_the_transports_can_report()
    {
        // Warning fica sem uso de proposito: nenhum transporte detecta "ainda
        // imprime, mas pede atencao" (plano §5.3).
        Assert.Equal(Contracts.StatusReportPrinterState.Ready, Worker.ToReportState(PrinterStatus.Ready));
        Assert.Equal(Contracts.StatusReportPrinterState.Error, Worker.ToReportState(PrinterStatus.Offline));
        Assert.Equal(Contracts.StatusReportPrinterState.Error, Worker.ToReportState(PrinterStatus.PaperOut));
        Assert.Equal(Contracts.StatusReportPrinterState.Error, Worker.ToReportState(PrinterStatus.CoverOpen));
        Assert.Equal(Contracts.StatusReportPrinterState.Unknown, Worker.ToReportState(PrinterStatus.Unknown));
    }
}
