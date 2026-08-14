using PrintAgent.Printing;
using PrintAgent.Tray.Ipc;

namespace PrintAgent.Tray;

/// <summary>
/// Tela de setup do Tray (plano §7.4, Fase 6; seções por estação, Fase 3 do
/// §10): pareamento, uma seção de impressora por estação (fila/IP:porta,
/// papel/code page, teste de impressão), log recente. Fala com o serviço só
/// pelo named pipe — nunca lê <c>agent.json</c> nem o token diretamente
/// (plano §7.2/§7.3), porque o serviço roda como <c>LocalSystem</c> e este
/// processo roda como o usuário logado.
/// </summary>
public sealed class SetupForm : Form
{
    private readonly AgentIpcClient _ipc;

    private readonly Label _summaryLabel;
    private readonly TextBox _codeBox;
    private readonly TextBox _deviceNameBox;
    private readonly Button _pairButton;
    private readonly Button _unpairButton;

    private readonly FlowLayoutPanel _printersContainer;
    private readonly List<PrinterSectionView> _printerSections = new();

    private readonly Button _exportDiagnosticsButton;

    private readonly RichTextBox _activityList;
    private readonly List<(string Text, Color Color)> _activityEntries = new();
    private const int MaxActivityEntries = 50;

    private sealed record CodePagePreset(string Label, int CodePage, int EscTIndex)
    {
        public override string ToString() => Label;
    }

    private sealed record PaperWidthOption(int Millimeters, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Estações do contrato (plano §10) mais a entrada "padrão" (<c>null</c>), pt-BR pronto pra combo.</summary>
    /// <summary>
    /// <paramref name="Label"/> é o texto do combo, que pode explicar o que a
    /// estação significa; <paramref name="ShortName"/> é como ela aparece no
    /// log de atividade. São separados porque a explicação que ajuda na hora
    /// de escolher polui a linha do log ("Cupom de teste enviado (\"Padrão —
    /// impressão padrão, sem roteamento específico\")").
    /// </summary>
    private sealed record StationOption(StationDto? Value, string Label, string ShortName)
    {
        public StationOption(StationDto? value, string label)
            : this(value, label, label)
        {
        }

        public override string ToString() => Label;
    }

    private static readonly StationOption[] StationOptions =
    [
        new(null, "Padrão — impressão padrão, sem roteamento", "Padrão"),
        new(StationDto.Kitchen, "Cozinha"),
        new(StationDto.Bar, "Bar"),
        new(StationDto.Counter, "Balcão / Caixa"),
        new(StationDto.Customer, "Cliente"),
    ];

    private static string GetStationLabel(StationDto? station) => StationOptions.First(o => o.Value == station).ShortName;

    /// <summary>Controles de uma seção "Impressora" (uma por estação configurada, plano §10).</summary>
    private sealed class PrinterSectionView
    {
        public required Panel Card { get; init; }
        public required ComboBox StationCombo { get; init; }
        public required ComboBox TransportCombo { get; init; }
        public required FlowLayoutPanel SpoolerRow { get; init; }
        public required ComboBox SpoolerNameCombo { get; init; }
        public required FlowLayoutPanel NetworkRow { get; init; }
        public required TextBox HostBox { get; init; }
        public required NumericUpDown PortUpDown { get; init; }
        public required ComboBox PaperWidthCombo { get; init; }
        public required ComboBox CodePageCombo { get; init; }
        public required CheckBox StripAccentsCheck { get; init; }
        public required NumericUpDown CopiesUpDown { get; init; }
        public required Button SaveButton { get; init; }
        public required Button TestPrintButton { get; init; }
        public required Button RemoveButton { get; init; }

        /// <summary>
        /// Estação sob a qual esta seção está de fato gravada no serviço, ou
        /// <c>false</c> em <see cref="IsPersisted"/> quando ela nunca foi
        /// salva (seção recém-criada pelo "+ Adicionar impressora"). Não dá
        /// para deduzir isso de <see cref="SelectedStation"/>: o combo reflete
        /// o que está na tela agora, que pode nunca ter sido gravado ou ter
        /// mudado desde o último "Salvar".
        /// </summary>
        public StationDto? PersistedStation { get; set; }

        public bool IsPersisted { get; set; }

        public StationDto? SelectedStation => ((StationOption)StationCombo.SelectedItem!).Value;
    }

    public SetupForm(AgentIpcClient ipc)
    {
        _ipc = ipc;

        Text = TitleFor(null);
        // Janela redimensionavel e minimizavel: com varias secoes de estacao a
        // tela passa da altura util do monitor, e com FixedDialog o unico jeito
        // de alcancar as secoes de baixo era rolar o painel interno.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(500, 400);

        // Altura desejada e a de conteudo completo, mas nunca maior que a area
        // util do monitor — senao a barra de titulo/borda inferior ficam fora
        // da tela e a janela nasce impossivel de redimensionar.
        var workingHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 900;
        ClientSize = new Size(480, Math.Min(860, workingHeight - 80));
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

        root.Controls.Add(SectionTitleLabel("Impressoras"));

        _printersContainer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        root.Controls.Add(_printersContainer);

        var addPrinterButton = new Button { Text = "+ Adicionar impressora", AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        addPrinterButton.Click += (_, _) => AddPrinterSection(initial: null);
        root.Controls.Add(addPrinterButton);

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

        _exportDiagnosticsButton = new Button { Text = "Exportar diagnóstico...", AutoSize = true };
        _exportDiagnosticsButton.Click += async (_, _) => await OnExportDiagnosticsAsync();

        root.Controls.Add(Section("Suporte", [
            new Label
            {
                AutoSize = true,
                MaximumSize = new Size(400, 0),
                Text = "Gera um arquivo .zip com a configuração, os registros de funcionamento "
                    + "e as últimas comandas, para enviar ao suporte do DiskPrato. "
                    + "O código de pareamento deste dispositivo não é incluído.",
            },
            ButtonRow(_exportDiagnosticsButton),
        ]));

        Load += async (_, _) => await OnLoadAsync();
    }

    private async Task OnLoadAsync()
    {
        var config = await _ipc.GetConfigAsync();
        if (!config.Ok)
        {
            LogActivity($"Não foi possível ler a configuração atual: {config.Error ?? "erro desconhecido"}.", success: false);
        }

        if (!config.Ok || config.Printers is null || config.Printers.Count == 0)
        {
            // Instalação nova ou config ilegível: sempre deixa pelo menos uma
            // seção editável na tela — nunca uma lista vazia sem jeito óbvio
            // de começar a configurar.
            AddPrinterSection(initial: null);
        }
        else
        {
            foreach (var printer in config.Printers)
            {
                AddPrinterSection(printer);
            }
        }

        ApplyStatus(config.Ok ? config.Status : null, config.Ok ? null : config.Error);
    }

    /// <summary>Chamado pelo <see cref="TrayApplicationContext"/> a cada rodada de polling — mantém o resumo no topo sempre atual mesmo com a tela aberta.</summary>
    public void OnStatusUpdated(IpcResponseDto response) => ApplyStatus(response.Ok ? response.Status : null, response.Ok ? null : response.Error);

    /// <summary>"Gerente de Impressão DiskPrato v1.1.1 — Configuração" — versão do
    /// serviço em execução (não a do Tray.exe), lida do status a cada polling,
    /// pra bater o olho e saber o que está instalado sem abrir o instalador.</summary>
    private static string TitleFor(AgentStatusDto? status)
    {
        const string baseTitle = "Gerente de Impressão DiskPrato";
        return string.IsNullOrWhiteSpace(status?.AgentVersion)
            ? $"{baseTitle} — Configuração"
            : $"{baseTitle} v{status.AgentVersion} — Configuração";
    }

    private void ApplyStatus(AgentStatusDto? status, string? error)
    {
        Text = TitleFor(status);

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

        // Este resumo reflete só a impressora "padrão" (Station == null), não
        // uma leitura agregada de todas as estações configuradas — mesma
        // limitação documentada no plano §10/§0 (Fase 3): o pipe ainda não
        // expõe status por estação, só por config (get-config).
        var connection = status.StreamConnected ? "conectado ao DiskPrato" : "sem conexão com o DiskPrato";
        _summaryLabel.Text =
            $"Pareado, {connection}.\n" +
            $"Impressora padrão ({status.Transport} — {status.PrinterTarget ?? "não configurada"}): {TranslatePrinterStatus(status.PrinterStatus)}.\n" +
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

    private void AddPrinterSection(PrinterConfigDto? initial)
    {
        var section = CreatePrinterSection(initial);
        _printerSections.Add(section);
        _printersContainer.Controls.Add(section.Card);
    }

    private PrinterSectionView CreatePrinterSection(PrinterConfigDto? initial)
    {
        var stationCombo = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var option in StationOptions)
        {
            stationCombo.Items.Add(option);
        }
        stationCombo.SelectedItem = StationOptions.First(o => o.Value == initial?.Station);

        var transportCombo = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        transportCombo.Items.Add("Fila do Windows (spooler)");
        transportCombo.Items.Add("Rede (IP:porta)");
        transportCombo.SelectedIndex = initial?.Transport == PrinterTransportKind.Network ? 1 : 0;

        var spoolerNameCombo = new ComboBox { Width = 250, DropDownStyle = ComboBoxStyle.DropDown };
        foreach (var name in SpoolerPrinterTransport.EnumPrinterQueues())
        {
            spoolerNameCombo.Items.Add(name);
        }
        if (!string.IsNullOrEmpty(initial?.SpoolerName) && !spoolerNameCombo.Items.Contains(initial.SpoolerName))
        {
            spoolerNameCombo.Items.Add(initial.SpoolerName);
        }
        spoolerNameCombo.Text = initial?.SpoolerName ?? "";
        var spoolerRow = LabeledRow("Fila de impressão", spoolerNameCombo);

        var hostBox = new TextBox { Width = 120, Text = initial?.Host ?? "" };
        var portUpDown = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 60 };
        portUpDown.Value = Math.Clamp(initial?.Port ?? 9100, (int)portUpDown.Minimum, (int)portUpDown.Maximum);
        var networkFields = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        networkFields.Controls.Add(new Label { Text = "IP", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        networkFields.Controls.Add(hostBox);
        networkFields.Controls.Add(new Label { Text = "Porta", AutoSize = true, Margin = new Padding(10, 6, 4, 0) });
        networkFields.Controls.Add(portUpDown);
        var networkRow = LabeledRow("Impressora de rede", networkFields);

        var paperWidthCombo = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        paperWidthCombo.Items.Add(new PaperWidthOption(80, "80mm — 48 colunas (fonte A)"));
        paperWidthCombo.Items.Add(new PaperWidthOption(58, "58mm — 32 colunas (fonte A)"));
        SelectPaperWidth(paperWidthCombo, initial?.PaperWidthMm ?? 80);

        var codePageCombo = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        codePageCombo.Items.Add(new CodePagePreset("CP850 — Europa Ocidental (padrão Epson, n=2)", 850, 2));
        codePageCombo.Items.Add(new CodePagePreset("CP860 — Português (n=3)", 860, 3));
        SelectCodePage(codePageCombo, initial?.CodePage ?? 850, initial?.EscTIndex ?? 2);

        var stripAccentsCheck = new CheckBox
        {
            Text = "Remover acentos (fallback se o cupom sair errado)",
            AutoSize = true,
            Checked = initial?.StripAccents ?? false,
        };
        var copiesUpDown = new NumericUpDown { Minimum = 1, Maximum = 5, Width = 60 };
        copiesUpDown.Value = Math.Clamp(initial?.Copies ?? 1, (int)copiesUpDown.Minimum, (int)copiesUpDown.Maximum);

        var saveButton = new Button { Text = "Salvar", AutoSize = true };
        var testPrintButton = new Button { Text = "Imprimir teste", AutoSize = true };
        var removeButton = new Button { Text = "Remover", AutoSize = true };

        void UpdateTransportVisibility()
        {
            var isNetwork = transportCombo.SelectedIndex == 1;
            spoolerRow.Visible = !isNetwork;
            networkRow.Visible = isNetwork;
        }
        transportCombo.SelectedIndexChanged += (_, _) => UpdateTransportVisibility();
        UpdateTransportVisibility();

        var card = Section("Impressora", [
            LabeledRow("Estação", stationCombo),
            LabeledRow("Transporte", transportCombo),
            spoolerRow,
            networkRow,
            LabeledRow("Papel", paperWidthCombo),
            LabeledRow("Code page", codePageCombo),
            Row(stripAccentsCheck),
            LabeledRow("Cópias", copiesUpDown),
            ButtonRow(saveButton, testPrintButton, removeButton),
        ]);

        var section = new PrinterSectionView
        {
            Card = card,
            StationCombo = stationCombo,
            TransportCombo = transportCombo,
            SpoolerRow = spoolerRow,
            SpoolerNameCombo = spoolerNameCombo,
            NetworkRow = networkRow,
            HostBox = hostBox,
            PortUpDown = portUpDown,
            PaperWidthCombo = paperWidthCombo,
            CodePageCombo = codePageCombo,
            StripAccentsCheck = stripAccentsCheck,
            CopiesUpDown = copiesUpDown,
            SaveButton = saveButton,
            TestPrintButton = testPrintButton,
            RemoveButton = removeButton,
            // Só uma seção vinda do get-config já existe do lado do serviço;
            // uma seção em branco ("+ Adicionar impressora") ainda não.
            IsPersisted = initial is not null,
            PersistedStation = initial?.Station,
        };

        saveButton.Click += async (_, _) => await OnSavePrinterAsync(section);
        testPrintButton.Click += async (_, _) => await OnTestPrintAsync(section);
        removeButton.Click += async (_, _) => await OnRemovePrinterAsync(section);

        return section;
    }

    private static void SelectPaperWidth(ComboBox combo, int paperWidthMm)
    {
        foreach (var item in combo.Items)
        {
            if (item is PaperWidthOption option && option.Millimeters == paperWidthMm)
            {
                combo.SelectedItem = item;
                return;
            }
        }

        var custom = new PaperWidthOption(paperWidthMm, $"{paperWidthMm}mm (personalizado)");
        combo.Items.Add(custom);
        combo.SelectedItem = custom;
    }

    private static void SelectCodePage(ComboBox combo, int codePage, int escTIndex)
    {
        foreach (var item in combo.Items)
        {
            if (item is CodePagePreset preset && preset.CodePage == codePage && preset.EscTIndex == escTIndex)
            {
                combo.SelectedItem = item;
                return;
            }
        }

        var custom = new CodePagePreset($"CP{codePage} (n={escTIndex}, personalizado)", codePage, escTIndex);
        combo.Items.Add(custom);
        combo.SelectedItem = custom;
    }

    private static PrinterConfigDto BuildDto(PrinterSectionView section)
    {
        var isNetwork = section.TransportCombo.SelectedIndex == 1;
        var codePage = (CodePagePreset)section.CodePageCombo.SelectedItem!;
        var paperWidth = (PaperWidthOption)section.PaperWidthCombo.SelectedItem!;

        return new PrinterConfigDto
        {
            Station = section.SelectedStation,
            Transport = isNetwork ? PrinterTransportKind.Network : PrinterTransportKind.Spooler,
            SpoolerName = isNetwork ? null : section.SpoolerNameCombo.Text.Trim(),
            Host = isNetwork ? section.HostBox.Text.Trim() : null,
            Port = (int)section.PortUpDown.Value,
            PaperWidthMm = paperWidth.Millimeters,
            CodePage = codePage.CodePage,
            EscTIndex = codePage.EscTIndex,
            StripAccents = section.StripAccentsCheck.Checked,
            Copies = (int)section.CopiesUpDown.Value,
        };
    }

    private async Task OnSavePrinterAsync(PrinterSectionView section)
    {
        var isNetwork = section.TransportCombo.SelectedIndex == 1;
        if (isNetwork && string.IsNullOrWhiteSpace(section.HostBox.Text))
        {
            LogActivity("Informe o IP da impressora de rede.");
            return;
        }

        if (!isNetwork && string.IsNullOrWhiteSpace(section.SpoolerNameCombo.Text))
        {
            LogActivity("Escolha a fila de impressão do Windows.");
            return;
        }

        var station = section.SelectedStation;
        if (_printerSections.Any(other => other != section && other.SelectedStation == station))
        {
            // set-printer faz upsert por estação (plano §10): salvar duas
            // seções com a mesma estação faria uma sobrescrever a outra em
            // silêncio no serviço — melhor barrar aqui do que deixar o
            // lojista descobrir isso só quando o cupom sair na impressora
            // errada.
            LogActivity($"Já existe outra impressora configurada para \"{GetStationLabel(station)}\" — escolha uma estação diferente antes de salvar.");
            return;
        }

        var printer = BuildDto(section);
        section.SaveButton.Enabled = false;
        try
        {
            var response = await _ipc.SetPrinterAsync(printer);
            if (response.Ok)
            {
                // set-printer faz upsert por estação: se o combo mudou desde o
                // último save, a entrada antiga continua lá com a estação
                // anterior. Remover a antiga aqui manteria "uma seção = uma
                // entrada", mas apagaria em silêncio a config de uma estação
                // que o lojista talvez ainda queira — melhor deixar as duas e
                // ele remover a que sobrou, que fica visível ao reabrir.
                section.IsPersisted = true;
                section.PersistedStation = station;
            }

            LogActivity(
                response.Ok ? $"Configuração da impressora \"{GetStationLabel(station)}\" salva." : $"Falha ao salvar: {response.Error}",
                response.Ok);
            ApplyStatus(response.Ok ? response.Status : null, response.Ok ? null : response.Error);
        }
        finally
        {
            section.SaveButton.Enabled = true;
        }
    }

    private async Task OnTestPrintAsync(PrinterSectionView section)
    {
        var station = section.SelectedStation;
        section.TestPrintButton.Enabled = false;
        try
        {
            var response = await _ipc.TestPrintAsync(station);
            LogActivity(
                response.Ok ? $"Cupom de teste enviado (\"{GetStationLabel(station)}\")." : $"Falha no teste de impressão: {response.Error}",
                response.Ok);
            ApplyStatus(response.Ok ? response.Status : null, response.Ok ? null : response.Error);
        }
        finally
        {
            section.TestPrintButton.Enabled = true;
        }
    }

    private async Task OnRemovePrinterAsync(PrinterSectionView section)
    {
        if (!section.IsPersisted)
        {
            // Seção que nunca foi salva não existe do lado do serviço: aqui
            // "Remover" é só desfazer o "+ Adicionar impressora". Mandar
            // remove-printer neste caso apagaria a impressora que já estava
            // gravada naquela estação (bug real: adicionar uma seção em
            // branco, cuja estação nasce "Padrão", e desistir dela levava
            // junto a impressora "Padrão" que estava funcionando).
            DropSection(section);
            LogActivity("Seção descartada (não estava salva).");
            return;
        }

        var confirm = MessageBox.Show(
            this, "Remover esta impressora da configuração?", "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        // A estação gravada, não a do combo: se o lojista trocou o combo sem
        // salvar, remover pela seleção atual apagaria outra estação (ou
        // nenhuma) e deixaria a gravada órfã na configuração.
        var station = section.PersistedStation;
        var response = await _ipc.RemovePrinterAsync(station);
        LogActivity(
            response.Ok ? $"Impressora \"{GetStationLabel(station)}\" removida." : $"Falha ao remover: {response.Error}",
            response.Ok);
        if (!response.Ok)
        {
            return;
        }

        DropSection(section);
        ApplyStatus(response.Status, null);
    }

    /// <summary>Tira a seção da tela mantendo a regra de nunca ficar sem nenhuma seção editável.</summary>
    private void DropSection(PrinterSectionView section)
    {
        _printersContainer.Controls.Remove(section.Card);
        _printerSections.Remove(section);

        if (_printerSections.Count == 0)
        {
            AddPrinterSection(initial: null);
        }
    }

    private async Task OnExportDiagnosticsAsync()
    {
        _exportDiagnosticsButton.Enabled = false;
        try
        {
            await DiagnosticsExportAction.RunAsync(this, _ipc, (message, ok) => LogActivity(message, ok));
        }
        finally
        {
            _exportDiagnosticsButton.Enabled = true;
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

    private static readonly Font SectionTitleFont = new("Segoe UI Semibold", 10.5f, FontStyle.Regular);
    private static readonly Color SectionTitleColor = Color.FromArgb(45, 45, 45);

    /// <summary>Título "solto" (fora de um card), usado para agrupar visualmente as N seções dinâmicas de impressora sem outro card à volta delas.</summary>
    private static Label SectionTitleLabel(string title) => new()
    {
        Text = title,
        AutoSize = true,
        Font = SectionTitleFont,
        ForeColor = SectionTitleColor,
        Margin = new Padding(0, 4, 0, 8),
    };

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
