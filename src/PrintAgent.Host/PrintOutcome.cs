namespace PrintAgent.Host;

/// <summary>Resultado de tentar processar um <c>PrintJob</c> (imediatamente ou num retry agendado).</summary>
public enum PrintOutcome
{
    /// <summary>Já estava em <c>printed</c> (dedup) — nada foi feito.</summary>
    AlreadyHandled,

    /// <summary>Impresso com sucesso; ack (ou pending-ack) já foi disparado.</summary>
    Printed,

    /// <summary>Falhou, mas ainda tem tentativa local disponível — fica na fila para o próximo retry agendado.</summary>
    Queued,

    /// <summary>Esgotou as tentativas locais; ack "failed" (ou pending-ack) já foi disparado.</summary>
    Failed,
}
