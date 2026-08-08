namespace PrintAgent.Transport;

/// <summary>
/// Leitura do header <c>Retry-After</c> (§6.6: "429: respeitar Retry-After se
/// vier; senão backoff"). Suporta tanto a forma em segundos (delta) quanto a
/// forma de data absoluta (HTTP-date), como o header permite.
/// </summary>
internal static class RetryAfterHelper
{
    public static TimeSpan? TryGet(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;

        if (retryAfter.Delta is { } delta) return delta;

        if (retryAfter.Date is { } date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }

        return null;
    }
}
