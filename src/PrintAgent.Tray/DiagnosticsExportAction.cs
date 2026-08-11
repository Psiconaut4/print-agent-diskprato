using System.Diagnostics;
using PrintAgent.Tray.Ipc;

namespace PrintAgent.Tray;

/// <summary>
/// Fluxo de "exportar diagnóstico" (plano §8, Fase 8), compartilhado pela tela
/// de setup e pelo menu da bandeja — os dois pontos de entrada precisam
/// escolher o destino, pedir o pacote ao serviço e oferecer abrir a pasta, e
/// duplicar isso deixaria os dois divergindo no nome do arquivo sugerido.
/// </summary>
public static class DiagnosticsExportAction
{
    /// <summary>
    /// <paramref name="report"/> recebe a mensagem e se deu certo — a tela de
    /// setup manda para o log de atividade, a bandeja para um balão.
    /// Cancelar o diálogo de salvar não chama <paramref name="report"/>.
    /// </summary>
    public static async Task RunAsync(IWin32Window? owner, AgentIpcClient ipc, Action<string, bool> report)
    {
        // Nome sugerido com maquina e horario: o suporte recebe pacotes de
        // varias lojas e, com "diagnostico.zip" em todos, nao consegue nem
        // guardar dois na mesma pasta.
        using var dialog = new SaveFileDialog
        {
            Title = "Salvar pacote de diagnóstico",
            Filter = "Pacote de diagnóstico (*.zip)|*.zip",
            DefaultExt = "zip",
            FileName = $"diskprato-diagnostico-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            OverwritePrompt = true,
        };

        var chosen = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (chosen != DialogResult.OK)
        {
            return;
        }

        var response = await ipc.ExportDiagnosticsAsync(dialog.FileName);
        if (!response.Ok)
        {
            report($"Falha ao exportar diagnóstico: {response.Error}", false);
            return;
        }

        var path = response.Path ?? dialog.FileName;
        report($"Diagnóstico exportado para {path}.", true);

        var open = MessageBox.Show(
            $"Pacote de diagnóstico salvo em:\n{path}\n\nEle contém dados de pedidos e de clientes — envie apenas ao suporte do DiskPrato.\n\nAbrir a pasta?",
            "Diagnóstico exportado",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (open == DialogResult.Yes)
        {
            OpenContainingFolder(path);
        }
    }

    private static void OpenContainingFolder(string path)
    {
        try
        {
            // /select deixa o arquivo ja destacado na janela — o lojista so
            // arrasta para o WhatsApp/e-mail.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // Nao abrir o Explorer nao invalida a exportacao: o arquivo esta
            // gravado e o caminho ja foi mostrado.
        }
    }
}
