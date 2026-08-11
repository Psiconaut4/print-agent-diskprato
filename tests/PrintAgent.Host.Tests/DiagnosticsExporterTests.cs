using System.IO.Compression;
using System.Text;
using PrintAgent.Host.Config;
using PrintAgent.Host.Diagnostics;
using PrintAgent.Host.Security;
using PrintAgent.Host.Storage;
using PrintAgent.Transport;

namespace PrintAgent.Host.Tests;

/// <summary>
/// Pacote de diagnóstico (plano §8, Fase 8). O teste que não pode faltar é o do
/// token: o pacote circula por WhatsApp e e-mail, e quem tem o
/// <c>deviceToken</c> recebe os pedidos do restaurante (plano §7.2).
/// </summary>
public class DiagnosticsExporterTests : IDisposable
{
    private const string Token = "tok_diagnostico_nao_pode_vazar_1234567890";

    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), $"printagent-test-{Guid.NewGuid():N}");
    private readonly string _logsDir;
    private readonly JobStore _jobStore;
    private readonly DiagnosticsExporter _exporter;

    public DiagnosticsExporterTests()
    {
        _logsDir = Path.Combine(_rootDir, "logs");
        Directory.CreateDirectory(_logsDir);

        var configStore = new AgentConfigStore(Path.Combine(_rootDir, "agent.json"));
        configStore.Save(new AgentConfig
        {
            DeviceId = "dev_123",
            Printers = [new PrinterConfig { Station = null, SpoolerName = "Balcao" }],
        });

        var tokenStore = new DeviceTokenStore(Path.Combine(_rootDir, "device.dat"));
        tokenStore.Save(Token);

        var pairingApi = new PairingApiClient(new HttpClient { BaseAddress = new Uri("https://example.invalid") });
        _jobStore = new JobStore(Path.Combine(_rootDir, "queue"));
        var controller = new AgentController(configStore, tokenStore, pairingApi, _jobStore);

        var selfTest = new StartupSelfTest(controller, _jobStore) { LogsDirectory = _logsDir };
        _exporter = new DiagnosticsExporter(controller, _jobStore, configStore, selfTest) { LogsDirectory = _logsDir };
    }

    public void Dispose() => Directory.Delete(_rootDir, recursive: true);

    private async Task<ZipArchive> BuildAsync()
    {
        var bytes = await _exporter.BuildAsync(CancellationToken.None);
        return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    }

    [Fact]
    public async Task Never_includes_the_device_token_in_any_form()
    {
        using var archive = await BuildAsync();

        // Duas garantias diferentes: o arquivo protegido nao entra, e o token em
        // claro nao aparece dentro de nenhuma entrada (agent.json, log, job).
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("device.dat", StringComparison.OrdinalIgnoreCase));

        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            Assert.DoesNotContain(Token, await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task Includes_config_readme_and_summary()
    {
        using var archive = await BuildAsync();

        Assert.Contains(archive.Entries, entry => entry.FullName == "agent.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "LEIA-ME.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "diagnostico.json");
    }

    [Fact]
    public async Task Includes_the_jobs_still_in_the_local_queue()
    {
        _jobStore.RecordReceived("job_1", "{\"jobId\":\"job_1\"}", DateTimeOffset.UtcNow);

        using var archive = await BuildAsync();

        Assert.Contains(archive.Entries, entry => entry.FullName == "fila/pending/job_1.json");
    }

    [Fact]
    public async Task Includes_the_rotated_log_files()
    {
        await File.WriteAllTextAsync(Path.Combine(_logsDir, $"{AgentLogging.FileNamePrefix}20260810.log"), "linha de ontem");
        await File.WriteAllTextAsync(Path.Combine(_logsDir, $"{AgentLogging.FileNamePrefix}20260811.log"), "linha de hoje");

        using var archive = await BuildAsync();

        Assert.Equal(2, archive.Entries.Count(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Survives_a_log_file_that_is_open_for_writing()
    {
        // O log do dia esta sempre aberto pelo proprio Serilog na hora da
        // exportacao: sem FileShare.ReadWrite isso derrubaria o pacote inteiro
        // justamente na maquina que se quer diagnosticar.
        var path = Path.Combine(_logsDir, $"{AgentLogging.FileNamePrefix}20260811.log");
        await using var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await held.WriteAsync(Encoding.UTF8.GetBytes("linha em uso"), CancellationToken.None);
        await held.FlushAsync(CancellationToken.None);

        using var archive = await BuildAsync();

        var entry = Assert.Single(archive.Entries, e => e.FullName == $"logs/{Path.GetFileName(path)}");
        using var reader = new StreamReader(entry.Open());
        Assert.Contains("linha em uso", await reader.ReadToEndAsync());
    }
}
