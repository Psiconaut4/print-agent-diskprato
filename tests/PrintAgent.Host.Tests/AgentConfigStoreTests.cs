using PrintAgent.Contracts;
using PrintAgent.Host.Config;

namespace PrintAgent.Host.Tests;

/// <summary>
/// Migração automática de <c>agent.json</c> pré-1.1 (campo singular
/// <c>printer</c>) para o formato de lista <c>printers</c> (plano §10).
/// </summary>
public class AgentConfigStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"printagent-test-{Guid.NewGuid():N}", "agent.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_migrates_legacy_singular_printer_into_a_one_element_list_with_null_station()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """
            {
              "apiBaseUrl": "https://api.diskprato.com",
              "deviceId": "clx123",
              "printer": {
                "transport": "spooler",
                "spoolerName": "EPSON TM-T20",
                "port": 9100,
                "paperWidthMm": 80,
                "codePage": 850,
                "escTIndex": 2,
                "stripAccents": false,
                "copies": 1
              }
            }
            """);

        var config = new AgentConfigStore(_path).Load();

        var printer = Assert.Single(config.Printers);
        Assert.Null(printer.Station);
        Assert.Equal("EPSON TM-T20", printer.SpoolerName);
        Assert.Equal("clx123", config.DeviceId);
    }

    [Fact]
    public void Load_persists_the_migrated_format_so_it_only_happens_once()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """{ "printer": { "spoolerName": "Fila Antiga" } }""");

        var store = new AgentConfigStore(_path);
        store.Load();

        // Reescrito em disco: a segunda carga já não depende mais de migração,
        // e o arquivo no disco reflete o formato novo (suporte pode abrir e
        // ver "printers", não mais "printer").
        var rewrittenJson = File.ReadAllText(_path);
        Assert.Contains("\"printers\"", rewrittenJson);
        Assert.DoesNotContain("\"printer\"", rewrittenJson);

        var reloaded = store.Load();
        var printer = Assert.Single(reloaded.Printers);
        Assert.Equal("Fila Antiga", printer.SpoolerName);
    }

    [Fact]
    public void Load_of_new_format_does_not_touch_printers_already_configured_per_station()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """
            {
              "printers": [
                { "station": "kitchen", "spoolerName": "Cozinha" },
                { "spoolerName": "Balcao" }
              ]
            }
            """);

        var config = new AgentConfigStore(_path).Load();

        Assert.Equal(2, config.Printers.Count);
        Assert.Equal(PrintJobTarget.Kitchen, config.Printers[0].Station);
        Assert.Null(config.Printers[1].Station);
    }

    [Fact]
    public void Load_of_missing_file_returns_default_config_with_no_printers()
    {
        var config = new AgentConfigStore(_path).Load();

        Assert.Empty(config.Printers);
    }
}
