namespace PrintAgent.Transport;

/// <summary>
/// Constrói os dois <see cref="HttpClient"/> que a camada Transport precisa
/// (§6.3): um para o stream SSE, com timeout infinito (o default de 100s
/// mata a conexão persistente), e outro para as chamadas HTTP normais
/// (pair/ack/status/jobs-pending), com timeout curto.
/// </summary>
public static class PrintAgentHttpClientFactory
{
    private static readonly TimeSpan DefaultApiTimeout = TimeSpan.FromSeconds(30);

    public static HttpClient CreateApiClient(
        Uri baseUrl,
        Func<string?> tokenAccessor,
        string agentVersion,
        HttpMessageHandler? innerHandler = null,
        TimeSpan? timeout = null)
    {
        var handler = new AuthHeaderHandler(tokenAccessor, agentVersion, innerHandler);
        return new HttpClient(handler)
        {
            BaseAddress = baseUrl,
            Timeout = timeout ?? DefaultApiTimeout,
        };
    }

    public static HttpClient CreateStreamClient(
        Uri baseUrl,
        Func<string?> tokenAccessor,
        string agentVersion,
        HttpMessageHandler? innerHandler = null)
    {
        var handler = new AuthHeaderHandler(tokenAccessor, agentVersion, innerHandler);
        return new HttpClient(handler)
        {
            BaseAddress = baseUrl,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
