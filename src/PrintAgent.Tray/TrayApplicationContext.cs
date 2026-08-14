using PrintAgent.Tray.Ipc;

namespace PrintAgent.Tray;

/// <summary>
/// Mantém o ícone na bandeja vivo sem nenhuma janela principal — o serviço
/// do Windows roda na Session 0 e não pode desenhar UI (plano §7.4), então
/// este é um processo separado, iniciado com o usuário logado, que só fala
/// com o serviço pelo named pipe.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const string AppName = "Gerente de Impressão DiskPrato";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly AgentIpcClient _ipc = new();
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private SetupForm? _setupForm;

    // Versão do serviço, lida a cada polling — não a do Tray.exe: os dois
    // vêm do mesmo instalador hoje, mas quem importa para diagnóstico é qual
    // versão do Host está de fato rodando (plano §7.4, Tray nunca assume
    // nada sobre o serviço fora do que o pipe reporta).
    private string? _lastKnownVersion;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Configurar...", null, (_, _) => ShowSetup());
        menu.Items.Add("Imprimir teste", null, async (_, _) => await RunTestPrintAsync());
        menu.Items.Add("Exportar diagnóstico...", null, async (_, _) => await RunExportDiagnosticsAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitApp());

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.Unknown,
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowSetup();

        _pollTimer = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _pollTimer.Start();

        _ = RefreshStatusAsync();
    }

    private void ShowSetup()
    {
        if (_setupForm is { IsDisposed: false })
        {
            _setupForm.Activate();
            return;
        }

        _setupForm = new SetupForm(_ipc);
        _setupForm.Show();
    }

    private async Task RunTestPrintAsync()
    {
        var result = await _ipc.TestPrintAsync();
        _icon.ShowBalloonTip(
            4000,
            DisplayName(),
            result.Ok ? "Cupom de teste enviado." : $"Falha no teste de impressão: {result.Error}",
            result.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        await RefreshStatusAsync();
    }

    /// <summary>
    /// Sem janela dona: o menu da bandeja não tem uma, e passar a tela de setup
    /// como owner (quando ela existe) faria o diálogo de salvar sumir junto se
    /// o lojista fechasse a tela no meio.
    /// </summary>
    private async Task RunExportDiagnosticsAsync() =>
        await DiagnosticsExportAction.RunAsync(null, _ipc, (message, ok) =>
            _icon.ShowBalloonTip(
                4000,
                DisplayName(),
                message,
                ok ? ToolTipIcon.Info : ToolTipIcon.Warning));

    private async Task RefreshStatusAsync()
    {
        var response = await _ipc.GetStatusAsync();
        var status = response.Ok ? response.Status : null;

        if (!string.IsNullOrWhiteSpace(status?.AgentVersion))
        {
            _lastKnownVersion = status.AgentVersion;
        }

        _icon.Icon = TrayIcons.For(status);
        _icon.Text = Truncate(Describe(status, response.Ok ? null : response.Error), 127);

        _setupForm?.OnStatusUpdated(response);
    }

    /// <summary>"Gerente de Impressão DiskPrato v1.1.1" — mesma convenção de
    /// serviços do Windows que mostram a versão ao lado do nome, para não
    /// precisar abrir o instalador/registro só pra saber o que está rodando.</summary>
    private string DisplayName() =>
        string.IsNullOrWhiteSpace(_lastKnownVersion) ? AppName : $"{AppName} v{_lastKnownVersion}";

    private string Describe(AgentStatusDto? status, string? error)
    {
        var name = DisplayName();

        if (status is null)
        {
            return $"{name}\n{error ?? "Serviço indisponível"}";
        }

        if (!status.Paired)
        {
            return $"{name}\nAguardando pareamento";
        }

        var connection = status.StreamConnected ? "conectado" : "sem conexão";
        return $"{name}\n{connection} — {status.QueuedJobs} na fila";
    }

    // NotifyIcon.Text é truncado silenciosamente pelo Windows acima de ~127
    // caracteres; corta aqui pra evitar um texto cortado no meio de uma palavra.
    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];

    private void ExitApp()
    {
        _pollTimer.Stop();
        _icon.Visible = false;
        Application.Exit();
    }
}
