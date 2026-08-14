using PrintAgent.Contracts;
using PrintAgent.Host.Config;
using PrintAgent.Host.Security;
using PrintAgent.Host.Storage;
using PrintAgent.Printing;
using PrintAgent.Transport;

namespace PrintAgent.Host;

/// <summary>Retrato do estado do agente para <c>get-status</c> no named pipe (plano §7.4).</summary>
public sealed record AgentStatusSnapshot(
    bool Paired,
    bool StreamConnected,
    int QueuedJobs,
    string Transport,
    string? PrinterTarget,
    string PrinterStatus,
    string AgentVersion);

/// <summary>
/// Estado compartilhado entre o <see cref="Worker"/> (loop de impressão) e o
/// servidor do named pipe (plano §7.4, consumido pelo Tray na Fase 6) — o
/// pipe nunca lê o token diretamente (plano §7.2), só pede status/ações ao
/// serviço por aqui.
/// </summary>
public sealed class AgentController
{
    private readonly object _lock = new();
    private readonly AgentConfigStore _configStore;
    private readonly DeviceTokenStore _tokenStore;
    private readonly PairingApiClient _pairingApi;
    private readonly JobStore _jobStore;

    private AgentConfig _config;
    private string? _token;

    public AgentController(
        AgentConfigStore configStore, DeviceTokenStore tokenStore, PairingApiClient pairingApi, JobStore jobStore)
    {
        _configStore = configStore;
        _tokenStore = tokenStore;
        _pairingApi = pairingApi;
        _jobStore = jobStore;
        _config = configStore.Load();
        _token = tokenStore.TryLoad();
    }

    /// <summary>Disparado quando o token muda (pareado, despareado, ou revogado) — o <see cref="Worker"/> reage reiniciando/parando o loop do stream.</summary>
    public event Action? TokenChanged;

    public AgentConfig Config
    {
        get { lock (_lock) return _config; }
    }

    public string? Token
    {
        get { lock (_lock) return _token; }
    }

    public bool IsPaired => Token is not null;

    /// <summary>Só o <see cref="Worker"/> escreve aqui, ao abrir/perder a conexão SSE.</summary>
    public bool StreamConnected { get; set; }

    public async Task<PairOutcome> PairAsync(
        string code, string deviceName, string agentVersion, string platform, CancellationToken ct)
    {
        var outcome = await _pairingApi.PairAsync(code, deviceName, agentVersion, platform, ct).ConfigureAwait(false);

        if (outcome is PairOutcome.Success success)
        {
            lock (_lock)
            {
                _token = success.Response.DeviceToken;
                _config.DeviceId = success.Response.DeviceId;
            }

            _tokenStore.Save(success.Response.DeviceToken);
            _configStore.Save(_config);
            TokenChanged?.Invoke();
        }

        return outcome;
    }

    public void Unpair()
    {
        lock (_lock) _token = null;
        _tokenStore.Clear();
        StreamConnected = false;
        TokenChanged?.Invoke();
    }

    /// <summary>401 terminal ou <c>device:revoked</c> (plano §6.4/§6.6): mesmo efeito de <see cref="Unpair"/>, gatilho diferente.</summary>
    public void InvalidateToken()
    {
        lock (_lock) _token = null;
        _tokenStore.Clear();
        StreamConnected = false;
        TokenChanged?.Invoke();
    }

    /// <summary>
    /// Upsert por estação (plano §10): o pipe manda uma <see cref="PrinterConfig"/>
    /// de cada vez (<c>set-printer</c>), com <see cref="PrinterConfig.Station"/>
    /// identificando qual entrada de <see cref="AgentConfig.Printers"/> ela
    /// substitui — não precisa remandar as outras estações. A tela de setup
    /// (Fase 3 do §10) edita uma seção por estação, cada uma salvando com sua
    /// própria chamada; a instalação de estação única de sempre continua
    /// mandando <c>Station = null</c> e editando a entrada "padrão".
    /// </summary>
    public void UpdatePrinterConfig(PrinterConfig printer)
    {
        lock (_lock)
        {
            var index = _config.Printers.FindIndex(p => p.Station == printer.Station);
            if (index >= 0)
            {
                _config.Printers[index] = printer;
            }
            else
            {
                _config.Printers.Add(printer);
            }
        }

        _configStore.Save(_config);
    }

    /// <summary>Remove a entrada de <paramref name="station"/> (plano §10 — Tray "remover estação"). Sem efeito se não existir.</summary>
    public void RemovePrinterConfig(PrintJobTarget? station)
    {
        lock (_lock)
        {
            _config.Printers.RemoveAll(p => p.Station == station);
        }

        _configStore.Save(_config);
    }

    /// <summary>
    /// Escolhe a impressora para um job de destino <paramref name="target"/>
    /// (plano §10): entrada dedicada àquela estação; senão a "padrão"
    /// (<c>Station == null</c>); senão a primeira da lista; senão uma config
    /// vazia (deixa o job em retry por "não configurado" em vez de derrubar o
    /// processamento) — nunca descarta um job por falta de impressora.
    /// </summary>
    public PrinterConfig ResolvePrinter(PrintJobTarget target)
    {
        var printers = Config.Printers;
        return printers.FirstOrDefault(p => p.Station == target)
            ?? ResolveDefaultPrinter(printers);
    }

    /// <summary>Impressora usada pelo pipe (status/config/teste) enquanto o Tray não edita por estação (Fase 3 do §10).</summary>
    public PrinterConfig ResolveDefaultPrinter() => ResolveDefaultPrinter(Config.Printers);

    private static PrinterConfig ResolveDefaultPrinter(IReadOnlyList<PrinterConfig> printers) =>
        printers.FirstOrDefault(p => p.Station is null)
        ?? printers.FirstOrDefault()
        ?? new PrinterConfig();

    /// <summary>
    /// Inclui uma leitura best-effort do estado físico da impressora (plano
    /// §5.3) — o ícone da bandeja e a tela de setup vivem disso, com um teto
    /// curto pra nunca travar o pipe numa impressora que não responde.
    /// </summary>
    public async Task<AgentStatusSnapshot> GetStatusAsync(CancellationToken ct)
    {
        var printer = ResolveDefaultPrinter();
        return new AgentStatusSnapshot(
            IsPaired,
            StreamConnected,
            _jobStore.GetQueueLength(),
            printer.Transport.ToString(),
            printer.Transport == PrinterTransportKind.Spooler ? printer.SpoolerName : printer.Host,
            (await QueryPrinterStatusAsync(printer, ct).ConfigureAwait(false)).ToString());
    }

    /// <summary>
    /// Mesma leitura que alimenta o ícone da bandeja, para quem precisa do
    /// enum e não do retrato inteiro — o <c>StatusReport</c> periódico do
    /// <see cref="Worker"/> (plano §6) e o auto-teste da inicialização.
    /// </summary>
    public Task<PrinterStatus> QueryDefaultPrinterStatusAsync(CancellationToken ct) =>
        QueryPrinterStatusAsync(ResolveDefaultPrinter(), ct);

    public static async Task<PrinterStatus> QueryPrinterStatusAsync(PrinterConfig printer, CancellationToken ct)
    {
        try
        {
            if (PrinterTransportFactory.Create(printer) is not IPrinterStatusQuery statusQuery)
            {
                return PrinterStatus.Unknown;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            return await statusQuery.QueryStatusAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // config incompleta (fila/host ainda nao escolhidos) ou driver que
            // lanca em vez de reportar erro: nunca deixa o pipe cair por isso.
            return PrinterStatus.Unknown;
        }
        catch (OperationCanceledException)
        {
            return PrinterStatus.Unknown;
        }
    }
}
