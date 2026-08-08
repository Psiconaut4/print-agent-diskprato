namespace PrintAgent.Core.Retry;

/// <summary>
/// Schedule de retry local para um job que falhou na impressão (plano §6.5:
/// "5 tentativas ao longo de ~10 min"). Não confundir com a política de
/// reconexão SSE do <c>PrintAgent.Transport</c> — são conceitos diferentes,
/// por isso o namespace próprio.
/// </summary>
public static class LocalPrintRetryPolicy
{
    /// <summary>
    /// Máximo de tentativas locais antes de desistir e reportar `failed` via ack.
    /// </summary>
    public const int MaxAttempts = 5;

    // O plano não fixa os intervalos exatos, só a janela (~10 min) e a
    // contagem (5 tentativas). Escolhido um schedule crescente em progressão
    // aritmética (passo de 30s, começando em 60s) que soma exatamente 600s
    // (10 min): 60s, 90s, 120s, 150s, 180s — dá tempo de um "PDV concorrente"
    // liberar a impressora sem segurar o job por mais que dez minutos no total.
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(90),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(150),
        TimeSpan.FromSeconds(180),
    ];

    /// <summary>
    /// Delay antes da próxima tentativa, dado o número da tentativa que acabou
    /// de falhar (1-based: 1 = primeira tentativa já feita, falhou).
    /// Retorna <see cref="TimeSpan.Zero"/> quando <paramref name="attemptNumber"/>
    /// já esgotou o schedule (não deveria ser chamado além de <see cref="MaxAttempts"/>).
    /// </summary>
    public static TimeSpan NextDelay(int attemptNumber)
    {
        if (attemptNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), attemptNumber, "attemptNumber deve ser >= 1.");
        }

        var index = attemptNumber - 1;
        return index < Delays.Length ? Delays[index] : TimeSpan.Zero;
    }
}
