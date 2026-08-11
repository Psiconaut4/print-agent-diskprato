using PrintAgent.Host.Config;
using PrintAgent.Host.Storage;
using PrintAgent.Printing;

namespace PrintAgent.Host.Diagnostics;

/// <summary>Uma linha do auto-teste. <see cref="Ok"/> nulo é "não dá pra afirmar" — nunca vira sucesso nem falha.</summary>
public sealed record SelfTestCheck(string Name, bool? Ok, string Detail);

/// <summary>
/// Auto-teste da inicialização (plano §8, Fase 8): na subida do serviço,
/// confere de uma vez tudo de que a impressão depende e escreve o resultado no
/// log. Existe porque o modo de falha típico do balcão não é um erro no meio da
/// operação, é o agente subir e ficar mudo — impressora renomeada no Windows,
/// pasta de dados sem permissão, dispositivo despareado por outro operador. Sem
/// isso, a primeira notícia do problema é uma comanda que não saiu no horário
/// de pico.
///
/// Deliberadamente não imprime nada: o serviço reinicia a cada boot da máquina
/// e a cada recuperação de crash (o instalador configura restart automático),
/// e um cupom de teste por reinício desperdiçaria papel e confundiria o
/// operador. Imprimir de verdade continua sendo o botão "Imprimir teste" do
/// Tray, disparado por gente.
/// </summary>
public sealed class StartupSelfTest(AgentController controller, JobStore jobStore)
{
    /// <summary>
    /// Injetável só para teste — em produção é sempre
    /// <see cref="AgentPaths.LogsDirectory"/>. Sem isto, rodar a suíte
    /// escreveria de verdade em <c>%ProgramData%\DiskPrato</c> da máquina de
    /// quem builda (o <c>JobStore</c> já recebe o diretório por construtor
    /// exatamente pelo mesmo motivo).
    /// </summary>
    public string LogsDirectory { get; init; } = AgentPaths.LogsDirectory;

    public async Task<IReadOnlyList<SelfTestCheck>> RunAsync(CancellationToken ct)
    {
        var checks = new List<SelfTestCheck>
        {
            CheckPairing(),
            CheckApiBaseUrl(),
            CheckWritable("Fila local", jobStore.RootDirectory),
            CheckWritable("Pasta de log", LogsDirectory),
        };

        var printers = controller.Config.Printers;
        if (printers.Count == 0)
        {
            checks.Add(new SelfTestCheck("Impressoras", false, "Nenhuma impressora configurada — abra a configuração na bandeja."));
        }

        foreach (var printer in printers)
        {
            checks.Add(await CheckPrinterAsync(printer, ct).ConfigureAwait(false));
        }

        return checks;
    }

    /// <summary>
    /// Escreve o resultado como um bloco de linhas seguidas, uma por check. Um
    /// único evento com a lista inteira seria mais limpo de consumir por
    /// máquina, mas quem lê este arquivo é uma pessoa do suporte com o Bloco de
    /// Notas aberto — linha a linha é o que se consegue ler e mandar por
    /// WhatsApp.
    /// </summary>
    public static void Log(ILogger logger, IReadOnlyList<SelfTestCheck> checks)
    {
        logger.LogInformation("Auto-teste da inicializacao (agente {AgentVersion}):", AgentVersion.Current);

        foreach (var check in checks)
        {
            var mark = check.Ok switch { true => "ok", false => "FALHA", null => "?" };

            // Falha vira Warning, nao Error: nenhuma destas condicoes impede o
            // servico de rodar (ele espera pareamento, espera configuracao,
            // reenfileira job), e um Error na subida de toda instalacao nova
            // ensinaria o suporte a ignorar Error.
            if (check.Ok == false)
            {
                logger.LogWarning("  [{Mark}] {Check}: {Detail}", mark, check.Name, check.Detail);
            }
            else
            {
                logger.LogInformation("  [{Mark}] {Check}: {Detail}", mark, check.Name, check.Detail);
            }
        }
    }

    private SelfTestCheck CheckPairing() =>
        controller.IsPaired
            ? new SelfTestCheck("Pareamento", true, $"Pareado (deviceId {controller.Config.DeviceId ?? "desconhecido"}).")
            : new SelfTestCheck("Pareamento", false, "Sem token — pareie pelo codigo do lojista na bandeja.");

    private SelfTestCheck CheckApiBaseUrl()
    {
        var url = controller.Config.ApiBaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new SelfTestCheck("Endereco da API", false, $"apiBaseUrl invalido no agent.json: \"{url}\".");
        }

        // Os caminhos dos clientes sao root-relative e ja trazem o /api
        // (/api/print-agents/v1/...), entao qualquer caminho aqui e descartado
        // em silencio na resolucao da URI: quem escreve ".../api" no agent.json
        // acha que apontou pro lugar certo e nao ve diferenca nenhuma.
        if (uri.AbsolutePath != "/")
        {
            return new SelfTestCheck(
                "Endereco da API",
                false,
                $"apiBaseUrl deve ser so a origem, sem caminho: \"{uri.AbsolutePath}\" sera ignorado. Use \"{uri.GetLeftPart(UriPartial.Authority)}\".");
        }

        // http:// funciona e as vezes e o que o suporte usa pra testar contra um
        // backend local; so nao pode passar despercebido, porque o token do
        // dispositivo viaja em todo request (plano §7.2).
        return uri.Scheme == Uri.UriSchemeHttps
            ? new SelfTestCheck("Endereco da API", true, uri.ToString())
            : new SelfTestCheck("Endereco da API", false, $"{uri} nao usa HTTPS — o token trafega sem cifra.");
    }

    private static SelfTestCheck CheckWritable(string name, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // Existir nao basta: a ACL restrita do %ProgramData% (plano §7.2) e
            // o disco cheio so aparecem na hora de escrever.
            var probe = Path.Combine(directory, $".selftest-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return new SelfTestCheck(name, true, directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SelfTestCheck(name, false, $"{directory} nao esta gravavel: {ex.Message}");
        }
    }

    private static async Task<SelfTestCheck> CheckPrinterAsync(PrinterConfig printer, CancellationToken ct)
    {
        var station = printer.Station?.ToString() ?? "padrao";
        var name = $"Impressora ({station})";

        var target = printer.Transport == PrinterTransportKind.Spooler ? printer.SpoolerName : printer.Host;
        if (string.IsNullOrWhiteSpace(target))
        {
            return new SelfTestCheck(name, false, "Sem fila/IP configurado.");
        }

        var status = await AgentController.QueryPrinterStatusAsync(printer, ct).ConfigureAwait(false);
        var where = $"{printer.Transport} \"{target}\"";

        return status switch
        {
            PrinterStatus.Ready => new SelfTestCheck(name, true, $"{where}: pronta."),
            PrinterStatus.Offline => new SelfTestCheck(name, false, $"{where}: offline."),
            PrinterStatus.PaperOut => new SelfTestCheck(name, false, $"{where}: sem papel."),
            PrinterStatus.CoverOpen => new SelfTestCheck(name, false, $"{where}: tampa aberta."),
            // Unknown nao e falha: driver generico "Text Only" nunca reporta
            // estado (plano §5.3), e a instalacao pode estar perfeita.
            _ => new SelfTestCheck(name, null, $"{where}: configurada, estado nao reportado pelo driver."),
        };
    }
}
