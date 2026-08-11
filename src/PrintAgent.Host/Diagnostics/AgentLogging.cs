using System.Text;
using Serilog;
using Serilog.Events;

namespace PrintAgent.Host.Diagnostics;

/// <summary>
/// Log em arquivo com rotação (plano §8, Fase 8). Um serviço do Windows não
/// tem console para ninguém ler: sem isso, a única pista de por que um cupom
/// não saiu na loja do cliente era o Visualizador de Eventos, que não recebe
/// nada além das transições de estado do próprio serviço.
///
/// Configurado em código, não pelo <c>appsettings.json</c> via
/// <c>Serilog.Settings.Configuration</c>: os dois <c>.exe</c> são publicados
/// self-contained single-file (plano §2), e aquele pacote descobre sinks
/// varrendo <c>DependencyContext</c>, que é justamente o que o single-file não
/// expõe de forma confiável. O único ajuste que faz sentido em campo — subir o
/// nível para <c>Debug</c> e reproduzir o problema — continua vindo do
/// <c>appsettings.json</c>, por <see cref="ResolveMinimumLevel"/>.
/// </summary>
public static class AgentLogging
{
    /// <summary>Nomeado por dia: <c>printagent-20260811.log</c> (plano §8 — rotação diária, retenção de 7 dias).</summary>
    public const string FileNamePrefix = "printagent-";

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Nunca lança: pasta sem permissão ou disco cheio degrada para log só de
    /// console em vez de impedir o serviço de subir. Um agente que não loga
    /// ainda imprime comandas; um agente que não sobe, não.
    /// </summary>
    public static Serilog.ILogger CreateLogger(IConfiguration configuration)
    {
        try
        {
            Directory.CreateDirectory(AgentPaths.LogsDirectory);
            return Build(configuration, withFile: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var logger = Build(configuration, withFile: false);
            logger.Error(ex, "Sem log em arquivo: nao foi possivel usar {LogsDirectory}.", AgentPaths.LogsDirectory);
            return logger;
        }
    }

    private static Serilog.ILogger Build(IConfiguration configuration, bool withFile)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Is(ResolveMinimumLevel(configuration))
            // O host emite uma linha por requisição HTTP interna e por
            // transição de BackgroundService; em Information isso afogaria as
            // linhas do agente, que são o motivo de o arquivo existir.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: OutputTemplate);

        if (withFile)
        {
            config = config.WriteTo.File(
                Path.Combine(AgentPaths.LogsDirectory, $"{FileNamePrefix}.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileTimeLimit: Retention,
                // Teto por arquivo além do teto por tempo: um erro em loop
                // (impressora recusando conexão a cada 15s) enche o disco do
                // balcão antes de o dia virar.
                fileSizeLimitBytes: 32L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 31,
                // O serviço morre por queda de energia com muito mais
                // frequência do que encerra de forma limpa, e as últimas
                // linhas antes da queda são exatamente as que interessam.
                flushToDiskInterval: TimeSpan.FromSeconds(2),
                // UTF-8 com BOM: quem abre isso e o suporte, no Bloco de Notas
                // do balcao. Sem BOM, "Sem token — pareie..." tem chance de
                // sair com acentuacao quebrada, e um log ilegivel nao serve
                // pra nada.
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                outputTemplate: OutputTemplate);
        }

        return config.CreateLogger();
    }

    /// <summary>
    /// Lê <c>Logging:LogLevel:Default</c> do <c>appsettings.json</c> — os nomes
    /// do <c>Microsoft.Extensions.Logging</c>, que é o que já está no arquivo e
    /// o que o suporte encontra ao pesquisar. Valor ausente ou irreconhecível
    /// cai em <c>Information</c>: um nível escrito errado nunca pode apagar o
    /// log inteiro justo na máquina que se tenta diagnosticar.
    /// </summary>
    internal static LogEventLevel ResolveMinimumLevel(IConfiguration configuration) =>
        configuration["Logging:LogLevel:Default"] switch
        {
            "Trace" => LogEventLevel.Verbose,
            "Debug" => LogEventLevel.Debug,
            "Warning" => LogEventLevel.Warning,
            "Error" => LogEventLevel.Error,
            "Critical" or "None" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        };
}
