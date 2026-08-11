using PrintAgent.Contracts;

namespace PrintAgent.Host.Ipc;

/// <summary>
/// Requisição JSON (uma linha por mensagem) do named pipe
/// <c>\\.\pipe\diskprato-printagent</c> (plano §7.4). <see cref="Command"/>:
/// <c>get-status</c>, <c>get-config</c>, <c>test-print</c>, <c>set-printer</c>,
/// <c>remove-printer</c>, <c>pair</c>, <c>unpair</c>,
/// <c>export-diagnostics</c>.
/// </summary>
public sealed class IpcRequest
{
    public string Command { get; set; } = string.Empty;

    // pair
    public string? Code { get; set; }
    public string? DeviceName { get; set; }

    // set-printer
    public Config.PrinterConfig? Printer { get; set; }

    /// <summary>
    /// <c>remove-printer</c>: qual estação remover de <see cref="Config.AgentConfig.Printers"/>.
    /// <c>test-print</c>: qual estação testar — ausente (null) testa a
    /// impressora "padrão" (plano §10), mesmo comportamento de antes desta
    /// estação existir no protocolo.
    /// </summary>
    public PrintJobTarget? Station { get; set; }

    /// <summary>
    /// <c>export-diagnostics</c>: onde gravar o <c>.zip</c>. O caminho é do
    /// cliente, e o serviço grava impersonando-o — ver
    /// <c>NamedPipeIpcServer.HandleExportDiagnosticsAsync</c>.
    /// </summary>
    public string? DestinationPath { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public AgentStatusSnapshot? Status { get; set; }

    /// <summary>
    /// Só preenchido em resposta a <c>get-config</c> (plano §7.4/§10) — a
    /// tela de setup precisa da lista inteira para desenhar uma seção por
    /// estação configurada, não só a impressora "padrão".
    /// </summary>
    public IReadOnlyList<Config.PrinterConfig>? Printers { get; set; }

    /// <summary>Só preenchido em resposta a <c>export-diagnostics</c>: onde o <c>.zip</c> foi efetivamente gravado.</summary>
    public string? Path { get; set; }

    public static IpcResponse Success(AgentStatusSnapshot? status = null, IReadOnlyList<Config.PrinterConfig>? printers = null) =>
        new() { Ok = true, Status = status, Printers = printers };

    public static IpcResponse Failure(string error) => new() { Ok = false, Error = error };
}
