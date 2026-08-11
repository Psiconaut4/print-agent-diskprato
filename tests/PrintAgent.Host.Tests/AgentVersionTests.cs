using System.Text.RegularExpressions;
using PrintAgent.Host.Diagnostics;

namespace PrintAgent.Host.Tests;

/// <summary>
/// Versão reportada ao backend (plano §8, Fase 8). Antes disso era a constante
/// <c>"1.0.0"</c> escrita à mão, que continuaria 1.0.0 depois de qualquer
/// release — e o backend decide por ela quem precisa atualizar
/// (<c>PRINT_AGENT_VERSION_UNSUPPORTED</c>, plano §6.6).
/// </summary>
public class AgentVersionTests
{
    [Fact]
    public void Current_is_plain_semver_without_build_metadata()
    {
        // O "+<sha>" que o InformationalVersion carrega quando o build tem
        // SourceLink nao pode vazar pro contrato.
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), AgentVersion.Current);
    }

    [Fact]
    public void Current_matches_the_version_declared_in_Directory_Build_props()
    {
        // Nao e a mesma coisa que o teste acima: um numero bem formado que nao
        // corresponde ao build (fallback "0.0.0", ou a versao de outro
        // assembly) passaria la e falha aqui.
        var assemblyVersion = typeof(AgentVersion).Assembly.GetName().Version;

        Assert.NotNull(assemblyVersion);
        Assert.Equal($"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}", AgentVersion.Current);
    }
}
