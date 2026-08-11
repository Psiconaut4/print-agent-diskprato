using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintAgent.Host.Config;

/// <summary>
/// Lê/escreve <c>agent.json</c> (plano §7.3). Arquivo, não registry: dá para
/// o suporte pedir print da tela por WhatsApp e diagnosticar em segundos.
/// </summary>
public sealed class AgentConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;

    public AgentConfigStore(string? path = null)
    {
        _path = path ?? Path.Combine(DefaultDirectory, "agent.json");
    }

    /// <summary><c>%ProgramData%\DiskPrato\PrintAgent</c> (plano §7).</summary>
    public static string DefaultDirectory => Diagnostics.AgentPaths.RootDirectory;

    public string ConfigPath => _path;

    public AgentConfig Load()
    {
        if (!File.Exists(_path))
        {
            return new AgentConfig();
        }

        var json = File.ReadAllText(_path);
        var config = JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions) ?? new AgentConfig();

        // Migração automática e silenciosa do formato pre-1.1 (plano §10): um
        // agent.json com o campo singular antigo "printer" e sem o "printers"
        // novo vira uma lista de um elemento, Station=null ("estação padrão,
        // recebe tudo") — nenhuma instalação existente perde configuração nem
        // precisa reconfigurar nada ao atualizar o agente.
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("printers", out _)
            && document.RootElement.TryGetProperty("printer", out var legacyPrinterElement))
        {
            var legacyPrinter = legacyPrinterElement.Deserialize<PrinterConfig>(JsonOptions) ?? new PrinterConfig();
            legacyPrinter.Station = null;
            config.Printers = [legacyPrinter];
            Save(config);
        }

        return config;
    }

    public void Save(AgentConfig config)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_path, json);
    }
}
