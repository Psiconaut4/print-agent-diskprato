namespace PrintAgent.Host.Config;

/// <summary>Conteúdo de <c>%ProgramData%\DiskPrato\PrintAgent\agent.json</c> (plano §7.3).</summary>
public sealed class AgentConfig
{
    public string ApiBaseUrl { get; set; } = "https://api.diskprato.com";

    /// <summary>Preenchido após o pareamento (plano §6.1). Null enquanto o dispositivo não foi pareado.</summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Uma impressora por estação (plano §10). Instalação de estação única
    /// (topologia 1 do §10, e todo agente de hoje) tem uma lista de um
    /// elemento só, com <see cref="PrinterConfig.Station"/> nulo — "recebe
    /// tudo". Formato antigo de <c>agent.json</c> (campo singular
    /// <c>printer</c>) é migrado automaticamente por
    /// <see cref="AgentConfigStore.Load"/>, nunca editado à mão aqui.
    /// </summary>
    public List<PrinterConfig> Printers { get; set; } = new();
}
