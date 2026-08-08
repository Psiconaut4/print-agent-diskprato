namespace PrintAgent.Host.Config;

/// <summary>Conteúdo de <c>%ProgramData%\DiskPrato\PrintAgent\agent.json</c> (plano §7.3).</summary>
public sealed class AgentConfig
{
    public string ApiBaseUrl { get; set; } = "https://api.diskprato.com";

    /// <summary>Preenchido após o pareamento (plano §6.1). Null enquanto o dispositivo não foi pareado.</summary>
    public string? DeviceId { get; set; }

    public PrinterConfig Printer { get; set; } = new();
}
