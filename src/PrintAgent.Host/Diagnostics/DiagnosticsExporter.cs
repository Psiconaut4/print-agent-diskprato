using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintAgent.Host.Config;
using PrintAgent.Host.Storage;

namespace PrintAgent.Host.Diagnostics;

/// <summary>
/// Monta o pacote de diagnóstico (plano §8, Fase 8): configuração, log e as
/// últimas comandas da fila local num único <c>.zip</c>, para o lojista mandar
/// ao suporte sem precisar navegar até <c>%ProgramData%</c> — pasta que ele
/// nem consegue abrir, porque o instalador restringe a ACL a SYSTEM +
/// Administradores (plano §7.2).
///
/// O <c>device.dat</c> nunca entra, em nenhuma circunstância. É o token do
/// dispositivo: quem o tem recebe os pedidos do restaurante, e um pacote de
/// diagnóstico circula por WhatsApp e e-mail. Por isso este tipo lista os
/// arquivos que quer, um a um, em vez de copiar a pasta de dados inteira e
/// excluir o que não deve ir — uma lista de exclusão erra por omissão quando
/// alguém adicionar o próximo arquivo sensível.
/// </summary>
public sealed class DiagnosticsExporter(
    AgentController controller, JobStore jobStore, AgentConfigStore configStore, StartupSelfTest selfTest)
{
    /// <summary>
    /// Teto por pasta da fila. O suficiente pra reconstruir o que aconteceu num
    /// turno; sem teto, uma loja com a fila entupida geraria um zip que não
    /// passa em anexo de e-mail nenhum.
    /// </summary>
    private const int MaxJobsPerFolder = 50;

    /// <summary>Injetável só para teste, como em <see cref="StartupSelfTest.LogsDirectory"/>.</summary>
    public string LogsDirectory { get; init; } = AgentPaths.LogsDirectory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<byte[]> BuildAsync(CancellationToken ct)
    {
        var checks = await selfTest.RunAsync(ct).ConfigureAwait(false);

        using var buffer = new MemoryStream();

        // O archive precisa ser fechado antes de ler o MemoryStream: o
        // diretorio central do zip so e gravado no Dispose.
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteText(archive, "LEIA-ME.txt", BuildReadme(checks));
            WriteText(archive, "diagnostico.json", JsonSerializer.Serialize(BuildSummary(checks), JsonOptions));
            CopyIfExists(archive, configStore.ConfigPath, "agent.json");
            CopyLogs(archive);
            CopyQueue(archive);
        }

        return buffer.ToArray();
    }

    private object BuildSummary(IReadOnlyList<SelfTestCheck> checks) => new
    {
        GeradoEm = DateTimeOffset.Now,
        AgentVersion = AgentVersion.Current,
        Maquina = Environment.MachineName,
        SistemaOperacional = Environment.OSVersion.VersionString,
        ProcessoIniciadoEm = TryGetProcessStart(),
        Pareado = controller.IsPaired,
        DeviceId = controller.Config.DeviceId,
        ApiBaseUrl = controller.Config.ApiBaseUrl,
        StreamConectado = controller.StreamConnected,
        JobsNaFila = jobStore.GetQueueLength(),
        AcksPendentes = jobStore.GetUnacknowledgedPrinted().Count + jobStore.GetUnacknowledgedFailed().Count,
        Impressoras = controller.Config.Printers,
        AutoTeste = checks,
    };

    private static DateTimeOffset? TryGetProcessStart()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string BuildReadme(IReadOnlyList<SelfTestCheck> checks)
    {
        var text = new StringBuilder();
        text.AppendLine("Pacote de diagnostico do DiskPrato Print Agent");
        text.AppendLine($"Gerado em {DateTimeOffset.Now:dd/MM/yyyy HH:mm:ss zzz} na maquina {Environment.MachineName}.");
        text.AppendLine();
        text.AppendLine("O que tem aqui dentro:");
        text.AppendLine("  diagnostico.json  estado do agente no momento da exportacao");
        text.AppendLine("  agent.json        configuracao (endereco da API e impressoras)");
        text.AppendLine("  logs/             registros de funcionamento dos ultimos 7 dias");
        text.AppendLine("  fila/             ultimas comandas: pendentes, impressas e falhadas");
        text.AppendLine();
        text.AppendLine("ATENCAO: os arquivos em fila/ sao comandas reais e contem dados de");
        text.AppendLine("pedidos e de clientes (nome, telefone e, nas entregas, endereco).");
        text.AppendLine("Compartilhe este pacote apenas com o suporte do DiskPrato.");
        text.AppendLine();
        text.AppendLine("O token do dispositivo NAO esta neste pacote, por seguranca.");
        text.AppendLine();
        text.AppendLine("Auto-teste no momento da exportacao:");
        foreach (var check in checks)
        {
            var mark = check.Ok switch { true => "ok   ", false => "FALHA", null => "?    " };
            text.AppendLine($"  [{mark}] {check.Name}: {check.Detail}");
        }

        return text.ToString();
    }

    private void CopyLogs(ZipArchive archive)
    {
        if (!Directory.Exists(LogsDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(LogsDirectory, $"{AgentLogging.FileNamePrefix}*.log"))
        {
            CopyIfExists(archive, path, $"logs/{Path.GetFileName(path)}");
        }
    }

    private void CopyQueue(ZipArchive archive)
    {
        foreach (var folder in new[] { "pending", "printed", "failed" })
        {
            var directory = Path.Combine(jobStore.RootDirectory, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            // Mais recentes primeiro: quando o teto corta, o que sobra e o que
            // aconteceu perto do problema que motivou a exportacao.
            var recent = new DirectoryInfo(directory)
                .EnumerateFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxJobsPerFolder);

            foreach (var file in recent)
            {
                CopyIfExists(archive, file.FullName, $"fila/{folder}/{file.Name}");
            }
        }
    }

    private static void WriteText(ZipArchive archive, string entryName, string content)
    {
        using var stream = archive.CreateEntry(entryName).Open();
        // UTF-8 com BOM: quem abre isso e o suporte, com o Bloco de Notas do
        // Windows, que sem BOM ainda erra a acentuacao de arquivo pequeno.
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.Write(content);
    }

    /// <summary>
    /// Best-effort por arquivo: um log rotacionando ou um job sendo movido de
    /// pasta bem na hora da exportação não pode derrubar o pacote inteiro —
    /// quem exporta está justamente com um problema para relatar.
    /// </summary>
    private static void CopyIfExists(ZipArchive archive, string sourcePath, string entryName)
    {
        try
        {
            // FileShare.ReadWrite porque o arquivo de log do dia esta aberto
            // pelo proprio processo, e a fila pode estar sendo reescrita pelo
            // loop de retry neste instante.
            using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var entry = archive.CreateEntry(entryName).Open();
            source.CopyTo(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteText(archive, $"{entryName}.ERRO.txt", $"Nao foi possivel incluir {sourcePath}: {ex.Message}");
        }
    }
}
