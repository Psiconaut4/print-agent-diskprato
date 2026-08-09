using PrintAgent.Printing;
using PrintAgent.Tray.Ipc;

namespace PrintAgent.Tray;

/// <summary>
/// Tela de setup do Tray (plano §7.4, Fase 6): pareamento, escolha de fila
/// ou IP:porta, papel/code page, teste de impressão, log recente. Fala com
/// o serviço só pelo named pipe — nunca lê <c>agent.json</c> nem o token
/// diretamente (plano §7.2/§7.3), porque o serviço roda como
/// <c>LocalSystem</c> e este processo roda como o usuário logado.
/// </summary>
public sealed class SetupForm : Form
{
    private readonly AgentIpcClient _ipc;

    private readonly Label _summaryLabel;
    private readonly TextBox _codeBox;
    private readonly TextBox _deviceNameBox;
    private readonly Button _pairButton;
    private readonly Button _unpairButton;

    private readonly ComboBox _transportCombo;
    private readonly FlowLayoutPanel _spoolerRow;
    private readonly ComboBox _spoolerNameCombo;
    private readonly FlowLayoutPanel _networkRow;
    private readonly TextBox _hostBox;
    private readonly NumericUpDown _portUpDown;
    private readonly ComboBox _paperWidthCombo;
    private readonly ComboBox _codePageCombo;
    private readonly CheckBox _stripAccentsCheck;
    private readonly NumericUpDown _copiesUpDown;
    private readonly Button _saveButton;
    private readonly Button _testPrintButton;

    private readonly RichTextBox _activityList;
    private readonly List<(string Text, Color Color)> _activityEntries = new();
    private const int MaxActivityEntries = 50;

    private sealed record CodePagePreset(string Label, int CodePage, int EscTIndex)
    {
        public override string ToString() => Label;
    }

    public SetupForm(AgentIpcClient ipc)
    {
        _ipc = ipc;

        Text = "Gerente de Impressão DiskPrato — Configuração";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 860);
        Icon = TrayIcons.Unknown;
        Padding = new Padding(10);
        BackColor = Color.FromArgb(240, 240, 240); // cinza claro por trás dos cards brancos das seções

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };
        Controls.Add(root);

        _summaryLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            Text = "Consultando o serviço...",
            Font = new Font(Font, FontStyle.Bold),
        };
        root.Controls.Add(Section("Estado", [_summaryLabel]));

        _codeBox = new TextBox { Width = 200 };
        _deviceNameBox = new TextBox { Width = 200, Text = Environment.MachineName };
        _pairButton = new Button { Text = "Parear", AutoSize = true };
        _pairButton.Click += async (_, _) => await OnPairAsync();
        _unpairButton = new Button { Text = "Desparear", AutoSize = true };
        _unpairButton.Click += async (_, _) => await OnUnpairAsync();

        root.Controls.Add(Section("Pareamento", [
            LabeledRow("Código do lojista", _codeBox),
            LabeledRow("Nome deste dispositivo", _deviceNameBox),
            ButtonRow(_pairButton, _unpairButton),
        ]));

        _transportCombo = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        _transportCombo.Items.Add("Fila do Windows (spooler)");
        _transportCombo.Items.Add("Rede (IP:porta)");
        _transportCombo.SelectedIndexChanged += (_, _) => UpdateTransportVisibility();

        _spoolerNameCombo = new ComboBox { Width = 250, DropDownStyle = ComboBoxStyle.DropDown };
        RefreshPrinterQueues();
        _spoolerRow = LabeledRow("Fila de impressão", _spoolerNameCombo);

        _hostBox = new TextBox { Width = 120 };
        _portUpDown = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 9100, Width = 60 };
        var networkFields = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        networkFields.Controls.Add(new Label { Text = "IP", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        networkFields.Controls.Add(_hostBox);
        networkFields.Controls.Add(new Label { Text = "Porta", AutoSize = true, Margin = new Padding(10, 6, 4, 0) });
        networkFields.Controls.Add(_portUpDown);
        _networkRow = LabeledRow("Impressora de rede", networkFields);

        _paperWidthCombo = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        _paperWidthCombo.Items.Add(new PaperWidthOption(80, "80mm — 48 colunas (fonte A)"));
        _paperWidthCombo.Items.Add(new PaperWidthOption(58, "58mm — 32 colunas (fonte A)"));

        _codePageCombo = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        _codePageCombo.Items.Add(new CodePagePreset("CP850 — Europa Ocidental (padrão Epson, n=2)", 850, 2));
        _codePageCombo.Items.Add(new CodePagePreset("CP860 — Português (n=3)", 860, 3));

        _stripAccentsCheck = new CheckBox { Text = "Remover acentos (fallback se o cupom sair errado)", AutoSize = true };
        _copiesUpDown = new NumericUpDown { Minimum = 1, Maximum = 5, Value = 1, Width = 60 };

        _saveButton = new Button { Text = "Salvar", AutoSize = true };
        _saveButton.Click += async (_, _) => await OnSaveAsync();
        _testPrintButton = new Button { Text = "Imprimir teste", AutoSize = true };
        _testPrintButton.Click += async (_, _) => await OnTestPrintAsync();

        root.Controls.Add(Section("Impressora", [
            LabeledRow("Transporte", _transportCombo),
            _spoolerRow,
            _networkRow,
            LabeledRow("Papel", _paperWidthCombo),
            LabeledRow("Code page", _codePageCombo),
            Row(_stripAccentsCheck),
            LabeledRow("Cópias", _copiesUpDown),
            ButtonRow(_saveButton, _testPrintButton),
        ]));

        _activityList = new RichTextBox
        {
            Width = 400,
            Height = 170,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            TabStop = false,
        };
        root.Controls.Add(Section("Atividade recente", [_activityList]));

        Load += async (_, _) => await OnLoadAsync();
    }

    private async Task OnLoadAsync()
    {
        var config = await _ipc.GetConfigAsync();
        if (!config.Ok || config.Printer is null)
        {
            LogActivity($"Não foi possível ler a configuração atual: {config.Error ?? "erro desconhecido"}.", success: false);
        }
        else
        {
            ApplyPrinterConfig(config.Printer);
        }

        UpdateTransportVisibility();
        ApplyStatus(config.Ok ? config.Status : null, config.Ok ? null : config.Error);
    }

    /// <summary>Chamado pelo <see cref="TrayApplicationContext"/> a cada rodada de polling — mantém o resumo no topo sempre atual mesmo com a tela aberta.</summary>
    public void OnStatusUpdated(IpcResponseDto response) => ApplyStatus(response.Ok ? response.Status : null, response.Ok ? null : response.Error);

    private void ApplyPrinterConfig(PrinterConfigDto printer)
    {
        _transportCombo.SelectedIndex = printer.Transport == PrinterTransportKind.Network ? 1 : 0;
        if (!string.IsNullOrEmpty(printer.SpoolerName) && !_spoolerNameCombo.Items.Contains(printer.SpoolerName))
        {
            _spoolerNameCombo.Items.Add(printer.SpoolerName);
        }
        _spoolerNameCombo.Text = printer.SpoolerName ?? "";
        _hostBox.Text = printer.Host ?? "";
        _portUpDown.Value = Math.Clamp(printer.Port, (int)_portUpDown.Minimum, (int)_portUpDown.Maximum);

        SelectPaperWidth(printer.PaperWidthMm);
        SelectCodePage(printer.CodePage, printer.EscTIndex);

        _stripAccentsCheck.Checked = printer.StripAccents;
        _copiesUpDown.Value = Math.Clamp(printer.Copies, (int)_copiesUpDown.Minimum, (int)_copiesUpDown.Maximum);
    }

    private void SelectPaperWidth(int paperWidthMm)
    {
        foreach (var item in _paperWidthCombo.Items)
        {
            if (item is PaperWidthOption option && option.Millimeters == paperWidthMm)
            {
                _paperWidthCombo.SelectedItem = item;
                return;
            }
        }

        var custom = new PaperWidthOption(paperWidthMm, $"{paperWidthMm}mm (personalizado)");
        _paperWidthCombo.Items.Add(custom);
        _paperWidthCombo.SelectedItem = custom;
    }

    private void SelectCodePage(int codePage, int escTIndex)
    {
        foreach (var item in _codePageCombo.Items)
        {
            if (item is CodePagePreset preset && preset.CodePage == codePage && preset.EscTIndex == escTIndex)
            {
                _codePageCombo.SelectedItem = item;
                return;
            }
        }

        var custom = new CodePagePreset($"CP{codePage} (n={escTIndex}, personalizado)", codePage, escTIndex);
        _codePageCombo.Items.Add(custom);
        _codePageCombo.SelectedItem = custom;
    }

    private void ApplyStatus(AgentStatusDto? status, string? error)
    {
        // Mesma paleta do ícone da bandeja (cinza/vermelho/laranja/verde) —
        // o resumo aqui e o ícone sempre concordam sobre o estado atual.
        _summaryLabel.ForeColor = TrayIcons.ColorFor(status);

        if (status is null)
        {
            _summaryLabel.Text = $"Serviço indisponível.\n{error}";
            _unpairButton.Enabled = false;
            return;
        }

        _unpairButton.Enabled = status.Paired;

        if (!status.Paired)
        {
            _summaryLabel.Text = "Não pareado — digite o código do lojista abaixo.";
            return;
        }

        var connection = status.StreamConnected ? "conectado ao DiskPrato" : "sem conexão com o DiskPrato";
        _summaryLabel.Text =
            $"Pareado, {connection}.\n" +
            $"Impressora ({status.Transport} — {status.PrinterTarget ?? "não configurada"}): {TranslatePrinterStatus(status.PrinterStatus)}.\n" +
            $"{status.QueuedJobs} pedido(s) na fila local.";
    }

    private static string TranslatePrinterStatus(string status) => status switch
    {
        "Ready" => "pronta",
        "Offline" => "offline",
        "PaperOut" => "sem papel",
        "CoverOpen" => "tampa aberta",
        _ => "estado desconhecido",
    };

    private void UpdateTransportVisibility()
    {
        var isNetwork = _transportCombo.SelectedIndex == 1;
        _spoolerRow.Visible = !isNetwork;
        _networkRow.Visible = isNetwork;
    }

    private void RefreshPrinterQueues()
    {
        _spoolerNameCombo.Items.Clear();
        foreach (var name in SpoolerPrinterTransport.EnumPrinterQueues())
        {
            _spoolerNameCombo.Items.Add(name);
        }
    }

    private async Task OnPairAsync()
    {
        var code = _codeBox.Text.Trim();
        var deviceName = _deviceNameBox.Text.Trim();
        if (code.Length == 0 || deviceName.Length == 0)
        {
            LogActivity("Preencha o código e o nome do dispositivo antes de parear.");
            return;
        }

        _pairButton.Enabled = false;
        try
        {
            var response = await _ipc.PairAsync(code, deviceName);
            if (response.Ok)
            {
                _codeBox.Clear();
                LogActivity("Pareado com sucesso.", success: true);
            }
            else
            {
                LogActivity($"Falha ao parear: {response.Error}", success: false);
            }

            ApplyStatus(response.Ok ? response.Status : null, response.Ok ? null : response.Error);
        }
        finally
        {
            _pairButton.Enabled = true;
        }
    }

    private async Task OnUnpairAsync()
    {
        var confirm = MessageBox.Show(
            this, "Desparear este dispositivo? Será preciso um novo código do lojista para voltar a imprimir.",
            "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        var response = await _ipc.UnpairAsync();
        LogActivity(response.Ok ? "Dispositivo despareado." : $"Falha ao desparear: {response.Error}", response.Ok);
        ApplyStatus(response.Ok ? response.Status : null, response.Ok ? null : response.Error);
    }

    private async Task OnSaveAsync()
    {
        var isNetwork = _transportCombo.SelectedIndex == 1;
        if (isNetwork && string.IsNullOrWhiteSpace(_hostBox.Text))
        {
            LogActivity("Informe o IP da impressora de rede.");
            return;
        }

        if (!isNetwork && string.IsNullOrWhiteSpace(_spoolerNameCombo.Text))
        {
            LogActivity("Escolha a fila de impressão do Windows.");
            return;
        }

        var codePage = (CodePagePreset)_codePageCombo.SelectedItem!;
        var paperWidth = (PaperWidthOption)_paperWidthCombo.SelectedItem!;

        var printer = new PrinterConfigDto
        {
            Transport = isNetwork ? PrinterTransportKind.Network : PrinterTransportKind.Spooler,
            SpoolerName = isNetwork ? null : _spoolerNameCombo.Text.Trim(),
            Host = isNetwork ? _hostBox.Text.Trim() : null,
            Port = (int)_portUpDown.Value,
            PaperWidthMm = paperWidth.Millimeters,
            CodePage = codePage.CodePage,
            EscTIndex = codePage.EscTIndex,
            StripAccents = _stripAccentsCheck.Checked,
            Copies = (int)_copiesUpDown.Value,
        };

        _saveButton.Enabled = false;
        try
        {
            var response = await _ipc.SetPrinterAsync(printer);
            LogActivity(response.Ok ? "Configuração da impressora salva." : $"Falha ao salvar: {response.Error}", response.Ok);
            ApplyStatus(response.Ok ? response.Status : null, response.Ok ? null : response.Error);
        }
        finally
        {
            _saveButton.Enabled = true;
        }
    }

    private async Task OnTestPrintAsync()
    {
        _testPrintButton.Enabled = false;
        try
        {
            var response = await _ipc.TestPrintAsync();
            LogActivity(response.Ok ? "Cupom de teste enviado." : $"Falha no teste de impressão: {response.Error}", response.Ok);
            ApplyStatus(response.Ok ? response.Status : null, response.Ok ? null : response.Error);
        }
        finally
        {
            _testPrintButton.Enabled = true;
        }
    }

    /// <summary><paramref name="success"/> colore a linha: <c>true</c> verde, <c>false</c> vermelho, <c>null</c> neutro (avisos de preenchimento, não são o resultado de uma ação).</summary>
    private void LogActivity(string message, bool? success = null)
    {
        var color = success switch
        {
            true => Color.SeaGreen,
            false => Color.Firebrick,
            null => Color.Black,
        };

        _activityEntries.Insert(0, ($"{DateTime.Now:HH:mm:ss} — {message}", color));
        if (_activityEntries.Count > MaxActivityEntries)
        {
            _activityEntries.RemoveAt(_activityEntries.Count - 1);
        }

        RenderActivity();
    }

    // RichTextBox não tem um equivalente a ListBox.Items.Insert por item —
    // mais simples e sempre correto redesenhar a lista inteira (no máximo
    // 50 linhas curtas) do que tentar achar offsets de linha pra colorir só
    // a nova entrada.
    private void RenderActivity()
    {
        _activityList.SuspendLayout();
        _activityList.Clear();
        foreach (var (text, color) in _activityEntries)
        {
            _activityList.SelectionStart = _activityList.TextLength;
            _activityList.SelectionLength = 0;
            _activityList.SelectionColor = color;
            _activityList.AppendText(text + Environment.NewLine);
        }
        _activityList.SelectionStart = 0;
        _activityList.SelectionLength = 0;
        _activityList.ResumeLayout();
    }

    private sealed record PaperWidthOption(int Millimeters, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly Font SectionTitleFont = new("Segoe UI Semibold", 10.5f, FontStyle.Regular);
    private static readonly Color SectionTitleColor = Color.FromArgb(45, 45, 45);

    // Um GroupBox aqui parece a escolha óbvia, mas AutoSize=true num GroupBox
    // com FlowLayoutPanel interno também AutoSize colapsa a legenda/borda de
    // forma imprevisível (bug real encontrado em validação manual — a caixa
    // vira uma coluna de um caractere de largura). Em vez de brigar com o
    // AutoSize embutido do GroupBox, monta-se o "card" à mão: um Panel com
    // borda fina + título em Label separado, cujo dimensionamento segue a
    // mesma regra que já provou funcionar no conteúdo (nunca combinar
    // AutoSize com Dock).
    private static Panel Section(string title, IEnumerable<Control> rows)
    {
        var titleLabel = new Label
        {
            Text = title,
            AutoSize = true,
            Font = SectionTitleFont,
            ForeColor = SectionTitleColor,
            Margin = new Padding(0, 0, 0, 10),
        };

        var rowsFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        foreach (var row in rows)
        {
            rowsFlow.Controls.Add(row);
        }

        var content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
        };
        content.Controls.Add(titleLabel);
        content.Controls.Add(rowsFlow);

        return new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            // MinimumSize, nao Width: uma largura fixa entraria em conflito
            // direto com AutoSize (o mesmo bug do GroupBox); MinimumSize so
            // estabelece um piso, deixando o autosize livre para crescer além
            // dele quando o conteúdo pedir.
            MinimumSize = new Size(440, 0),
            // Anchor Left+Right (sem Bottom) trava a largura do card exatamente
            // na largura disponível do FlowLayoutPanel pai — inclusive quando a
            // barra de rolagem vertical aparece e reduz essa largura — em vez de
            // deixar o card crescer além dela e forçar rolagem lateral no
            // formulário inteiro. A altura continua livre (AutoSize) porque
            // Bottom não está ancorado.
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 12),
            Controls = { content },
        };
    }

    private static FlowLayoutPanel LabeledRow(string label, Control control)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 140, Margin = new Padding(0, 6, 8, 0) });
        row.Controls.Add(control);
        return row;
    }

    private static FlowLayoutPanel Row(Control control)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        row.Controls.Add(control);
        return row;
    }

    private static FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        foreach (var button in buttons)
        {
            button.Margin = new Padding(0, 0, 8, 0);
            row.Controls.Add(button);
        }

        return row;
    }
}
