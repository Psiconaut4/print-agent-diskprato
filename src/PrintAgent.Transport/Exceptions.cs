namespace PrintAgent.Transport;

/// <summary>
/// Base para os poucos erros que a camada Transport julga "terminais" o
/// bastante para virar exceção em vez de retry silencioso (ver §6.6).
/// </summary>
public class PrintAgentTransportException : Exception
{
    public PrintAgentTransportException(string message) : base(message)
    {
    }

    public PrintAgentTransportException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// 401 em qualquer rota de dispositivo: token inválido ou revogado. Esta
/// camada não apaga o token (isso é storage/DPAPI, fora do escopo do
/// Transport) — apenas sinaliza para quem chamou parar de tentar.
/// </summary>
public sealed class PrintAgentUnauthorizedException : PrintAgentTransportException
{
    public PrintAgentUnauthorizedException()
        : base("Device token invalido ou revogado (401). Pareamento novo e necessario.")
    {
    }
}

/// <summary>
/// 400 com code=PRINT_AGENT_VERSION_UNSUPPORTED: a versão instalada do agente
/// não é mais servida pelo backend. Parar e avisar a UI que precisa atualizar.
/// </summary>
public sealed class PrintAgentVersionUnsupportedException : PrintAgentTransportException
{
    public PrintAgentVersionUnsupportedException()
        : base("Versao do agente nao e mais suportada pela API (PRINT_AGENT_VERSION_UNSUPPORTED).")
    {
    }
}
