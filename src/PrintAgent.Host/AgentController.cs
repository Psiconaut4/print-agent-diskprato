using PrintAgent.Host.Config;
using PrintAgent.Host.Security;
using PrintAgent.Host.Storage;
using PrintAgent.Transport;

namespace PrintAgent.Host;

/// <summary>Retrato do estado do agente para <c>get-status</c> no named pipe (plano §7.4).</summary>
public sealed record AgentStatusSnapshot(
    bool Paired, bool StreamConnected, int QueuedJobs, string Transport, string? PrinterTarget);

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

    public void UpdatePrinterConfig(PrinterConfig printer)
    {
        lock (_lock)
        {
            _config.Printer = printer;
        }

        _configStore.Save(_config);
    }

    public AgentStatusSnapshot GetStatus()
    {
        var config = Config;
        return new AgentStatusSnapshot(
            IsPaired,
            StreamConnected,
            _jobStore.GetQueueLength(),
            config.Printer.Transport.ToString(),
            config.Printer.Transport == PrinterTransportKind.Spooler ? config.Printer.SpoolerName : config.Printer.Host);
    }
}
