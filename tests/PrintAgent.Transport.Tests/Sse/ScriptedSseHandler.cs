namespace PrintAgent.Transport.Tests.Sse;

/// <summary>
/// Handler fake que devolve uma resposta scriptada por chamada de
/// <c>SendAsync</c>, uma por (re)conexão do <see cref="Transport.Sse.SseStreamClient"/>.
/// Grava as requisições recebidas para assertar headers (Last-Event-ID) nas
/// reconexões subsequentes. Se o script acabar, a próxima chamada fica
/// pendurada até o CancellationToken ser cancelado — como uma conexão real
/// que nunca respondeu.
/// </summary>
internal sealed class ScriptedSseHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly object _gate = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        lock (_gate) _responses.Enqueue(factory);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate) Requests.Add(request);

        Func<HttpRequestMessage, HttpResponseMessage>? factory;
        lock (_gate)
        {
            factory = _responses.Count > 0 ? _responses.Dequeue() : null;
        }

        if (factory is null)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }

        return Task.FromResult(factory(request));
    }

    public int RequestCount
    {
        get { lock (_gate) return Requests.Count; }
    }

    public IReadOnlyList<string?> LastEventIdHeaders
    {
        get
        {
            lock (_gate)
            {
                return Requests
                    .Select(r => r.Headers.TryGetValues("Last-Event-ID", out var values) ? values.FirstOrDefault() : null)
                    .ToList();
            }
        }
    }
}
