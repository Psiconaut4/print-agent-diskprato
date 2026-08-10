namespace PrintAgent.Tray.Ipc;

/// <summary>
/// Espelha <c>PrintAgent.Host.Config.PrinterTransportKind</c> pelo valor
/// numérico do enum, não pelo tipo C# — o Tray fala com o serviço só pelo
/// JSON do named pipe (plano §7.4), nunca por referência de projeto.
/// </summary>
public enum PrinterTransportKind
{
    Spooler = 0,
    Network = 1,
}

/// <summary>
/// Espelha <c>PrintAgent.Contracts.PrintJobTarget</c> pelo valor numérico do
/// enum — mesma lógica de <see cref="PrinterTransportKind"/>: o Tray e o
/// serviço trocam <c>IpcRequest</c>/<c>IpcResponse</c> sem
/// <c>JsonStringEnumConverter</c> nenhum (plano §7.4/§10), então o wire é
/// sempre o inteiro do enum, nunca o nome usado no contrato OpenAPI (que só
/// importa pra serialização de <c>PrintJob</c>, nunca atravessa o pipe).
/// </summary>
public enum StationDto
{
    Kitchen = 0,
    Bar = 1,
    Counter = 2,
    Customer = 3,
}

/// <summary>Espelha <c>PrintAgent.Host.Config.PrinterConfig</c> — mesmos nomes de campo, serialização padrão do <c>System.Text.Json</c> nos dois lados (plano §7.3/§10).</summary>
public sealed class PrinterConfigDto
{
    /// <summary><c>null</c> é a estação "padrão" — recebe qualquer job cujo <c>target</c> não tenha impressora própria (plano §10).</summary>
    public StationDto? Station { get; set; }
    public PrinterTransportKind Transport { get; set; } = PrinterTransportKind.Spooler;
    public string? SpoolerName { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 9100;
    public int PaperWidthMm { get; set; } = 80;
    public int CodePage { get; set; } = 850;
    public int EscTIndex { get; set; } = 2;
    public bool StripAccents { get; set; }
    public int Copies { get; set; } = 1;
}

/// <summary>Espelha <c>PrintAgent.Host.AgentStatusSnapshot</c>.</summary>
public sealed class AgentStatusDto
{
    public bool Paired { get; set; }
    public bool StreamConnected { get; set; }
    public int QueuedJobs { get; set; }
    public string Transport { get; set; } = "";
    public string? PrinterTarget { get; set; }
    public string PrinterStatus { get; set; } = "Unknown";
}

/// <summary>Espelha <c>PrintAgent.Host.Ipc.IpcRequest</c>.</summary>
public sealed class IpcRequestDto
{
    public string Command { get; set; } = "";

    // pair
    public string? Code { get; set; }
    public string? DeviceName { get; set; }

    // set-printer
    public PrinterConfigDto? Printer { get; set; }

    /// <summary>remove-printer: estação a remover. test-print: estação a testar (null = impressora "padrão").</summary>
    public StationDto? Station { get; set; }
}

/// <summary>Espelha <c>PrintAgent.Host.Ipc.IpcResponse</c>.</summary>
public sealed class IpcResponseDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public AgentStatusDto? Status { get; set; }

    /// <summary>Só preenchido em resposta a <c>get-config</c> — uma entrada por estação configurada (plano §10).</summary>
    public List<PrinterConfigDto>? Printers { get; set; }
}
