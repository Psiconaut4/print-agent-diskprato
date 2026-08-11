using System.Reflection;

namespace PrintAgent.Host.Diagnostics;

/// <summary>
/// Versão do agente reportada ao backend (cabeçalho <c>User-Agent</c>,
/// <c>agentVersion</c> do pareamento e do <c>StatusReport</c>) e mostrada no
/// diagnóstico. Lida do assembly, cuja versão vem do
/// <c>&lt;Version&gt;</c> do <c>Directory.Build.props</c> — a mesma que vira a
/// <c>ProductVersion</c> do <c>.msi</c>.
///
/// Antes da Fase 8 era a constante <c>"1.0.0"</c> repetida em três lugares, o
/// que fazia todo agente do parque instalado se anunciar como 1.0.0 para
/// sempre — e o backend depende disso para saber quem precisa atualizar
/// (<c>PRINT_AGENT_VERSION_UNSUPPORTED</c>, plano §6.6).
/// </summary>
public static class AgentVersion
{
    /// <summary>Sempre no formato <c>x.y.z</c>, sem metadados de build.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetAssembly(typeof(AgentVersion));

        // InformationalVersion e o que carrega o <Version> do projeto; ele vem
        // com "+<sha>" quando o build tem SourceLink, e o backend espera
        // semver simples.
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        // AssemblyVersion e sempre x.y.z.w; o ".w" nao interessa a ninguem.
        var version = assembly?.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
