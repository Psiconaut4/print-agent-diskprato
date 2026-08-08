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
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DiskPrato",
        "PrintAgent");

    public string ConfigPath => _path;

    public AgentConfig Load()
    {
        if (!File.Exists(_path))
        {
            return new AgentConfig();
        }

        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions) ?? new AgentConfig();
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
