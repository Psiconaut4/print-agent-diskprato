namespace PrintAgent.Transport;

/// <summary>
/// Backoff exponencial com jitter, usado tanto pelo cliente SSE (reconexão)
/// quanto pelas chamadas HTTP simples (retry em 5xx/erro de rede).
///
/// Formula: min(maxDelay, baseDelay * 2^(attempt-1)) * (1 + jitter), com
/// jitter uniforme em [-jitterRatio, +jitterRatio]. O jitter existe para que
/// todas as lojas não reconectem no mesmo milissegundo após uma queda da API
/// (ver docs/plan/PRINT-AGENT-REPO.md §6.3).
/// </summary>
public sealed class RetryBackoffCalculator
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _jitterRatio;
    private readonly Func<double> _randomSource;

    public RetryBackoffCalculator(
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        double jitterRatio = 0.2,
        Func<double>? randomSource = null)
    {
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(60);
        _jitterRatio = jitterRatio;
        _randomSource = randomSource ?? (() => Random.Shared.NextDouble());
    }

    /// <summary>
    /// Calcula o atraso para a N-ésima tentativa (1-based).
    /// </summary>
    public TimeSpan Next(int attempt)
    {
        if (attempt < 1) attempt = 1;

        var exponent = Math.Min(attempt - 1, 32); // evita overflow de Math.Pow
        var raw = _baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(raw, _maxDelay.TotalMilliseconds);

        // _randomSource() em [0,1) -> jitterFactor em [1-ratio, 1+ratio)
        var jitterFactor = 1.0 + ((_randomSource() * 2.0 - 1.0) * _jitterRatio);
        var final = capped * jitterFactor;

        return TimeSpan.FromMilliseconds(Math.Max(0, final));
    }
}
