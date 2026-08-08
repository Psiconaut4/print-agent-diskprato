namespace PrintAgent.Host.Ipc;

/// <summary>
/// Requisição JSON (uma linha por mensagem) do named pipe
/// <c>\\.\pipe\diskprato-printagent</c> (plano §7.4). <see cref="Command"/>:
/// <c>get-status</c>, <c>test-print</c>, <c>set-printer</c>, <c>pair</c>,
/// <c>unpair</c>.
/// </summary>
public sealed class IpcRequest
{
    public string Command { get; set; } = string.Empty;

    // pair
    public string? Code { get; set; }
    public string? DeviceName { get; set; }

    // set-printer
    public Config.PrinterConfig? Printer { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public AgentStatusSnapshot? Status { get; set; }

    /// <summary>Só preenchido em resposta a <c>get-config</c> (plano §7.4/Fase 6) — a tela de setup precisa dos valores atuais para pré-preencher os campos.</summary>
    public Config.PrinterConfig? Printer { get; set; }

    public static IpcResponse Success(AgentStatusSnapshot? status = null, Config.PrinterConfig? printer = null) =>
        new() { Ok = true, Status = status, Printer = printer };

    public static IpcResponse Failure(string error) => new() { Ok = false, Error = error };
}
