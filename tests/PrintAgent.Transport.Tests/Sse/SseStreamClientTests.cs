using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using PrintAgent.Transport.Sse;

namespace PrintAgent.Transport.Tests.Sse;

public class SseStreamClientTests
{
    // SSE `data:` só cobre uma linha por vez no parser (§6.3): o JSON precisa
    // estar minificado numa linha só, senão as linhas internas do JSON
    // "quebram" o framing (não começam com `data: `). O literal abaixo fica
    // legível para revisão e é minificado em runtime via CompactJson.
    private const string MinimalPrintJobJsonPretty = """
        {
          "jobId": "job-1",
          "orderId": "order-1",
          "restaurantId": "rest-1",
          "kind": "order",
          "target": "kitchen",
          "copies": 1,
          "issuedAt": "2026-08-08T12:00:00Z",
          "restaurant": { "name": "Restaurante Teste" },
          "order": {
            "number": "1001",
            "createdAt": "2026-08-08T12:00:00Z",
            "fulfillmentType": "pickup",
            "customer": { "name": "Fulano", "phone": "11999999999" },
            "payment": { "method": "cash", "status": "paid", "label": "Dinheiro" },
            "items": [ { "quantity": 1, "name": "X-Salada", "unitPriceCents": 2000, "totalPriceCents": 2000 } ],
            "subtotalCents": 2000,
            "deliveryFeeCents": 0,
            "totalCents": 2000,
            "currency": "BRL"
          }
        }
        """;

    private static readonly string MinimalPrintJobJson = JsonNode.Parse(MinimalPrintJobJsonPretty)!.ToJsonString();

    private static HttpResponseMessage StreamResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static HttpResponseMessage HangingStreamResponse(string initialBody)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HangingReadStream(initialBody)),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition not met within timeout.");
    }

    [Fact]
    public async Task ReconnectAfterDrop_ResendsLastProcessedEventId()
    {
        var handler = new ScriptedSseHandler();

        // Conexão 1: connected(e0) + print:job(e1), depois o stream termina (queda).
        handler.Enqueue(_ => StreamResponse(SseFrames.Concat(
            SseFrames.Frame("e0", "connected", """{"deviceId":"dev-1"}"""),
            SseFrames.Frame("e1", "print:job", MinimalPrintJobJson))));

        // Conexão 2: só para capturar o header Last-Event-ID enviado.
        handler.Enqueue(_ => StreamResponse(SseFrames.Frame("e2", "connected", """{"deviceId":"dev-1"}""")));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test"), Timeout = Timeout.InfiniteTimeSpan };
        var fakeTime = new FakeTimeProvider();

        // jitter neutro (0.5 -> fator 1.0) para backoff determinístico nos testes.
        var backoff = new RetryBackoffCalculator(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 0.2, () => 0.5);
        var client = new SseStreamClient(http, fakeTime, TimeSpan.FromSeconds(90), backoff);

        string? markedEventId = null;
        client.JobReceived += e =>
        {
            markedEventId = e.EventId;
            client.MarkProcessed(e.EventId!);
        };

        using var cts = new CancellationTokenSource();
        var runTask = client.RunAsync(cts.Token);

        await WaitUntilAsync(() => markedEventId == "e1", TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => handler.RequestCount == 1, TimeSpan.FromSeconds(5));

        // deixa a 1a conexão terminar de ler (EOF) e o loop chegar ao Task.Delay do backoff
        // antes de avançar o relógio fake.
        await Task.Delay(100);

        // avança o relógio fake além do backoff da 1a tentativa (~1s) para liberar a reconexão.
        fakeTime.Advance(TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => handler.RequestCount == 2, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await runTask;

        var headers = handler.LastEventIdHeaders;
        Assert.Null(headers[0]); // primeira conexão: sem Last-Event-ID
        Assert.Equal("e1", headers[1]); // reconexão: reenvia o último id confirmado (job já processado)
    }

    [Fact]
    public async Task Watchdog_TriggersReconnect_WhenNoFrameWithin90Seconds()
    {
        var handler = new ScriptedSseHandler();

        // Conexão 1: manda connected e depois fica pendurada (sem ping, sem nada).
        handler.Enqueue(_ => HangingStreamResponse(
            SseFrames.Frame("e0", "connected", """{"deviceId":"dev-1"}""")));

        // Conexão 2: confirma que o watchdog levou a uma reconexão.
        handler.Enqueue(_ => StreamResponse(
            SseFrames.Frame("e1", "connected", """{"deviceId":"dev-1"}""")));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test"), Timeout = Timeout.InfiniteTimeSpan };
        var fakeTime = new FakeTimeProvider();
        var backoff = new RetryBackoffCalculator(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 0.2, () => 0.5);
        var watchdogTimeout = TimeSpan.FromSeconds(90);
        var client = new SseStreamClient(http, fakeTime, watchdogTimeout, backoff);

        using var cts = new CancellationTokenSource();
        var runTask = client.RunAsync(cts.Token);

        await WaitUntilAsync(() => handler.RequestCount == 1, TimeSpan.FromSeconds(5));

        // sem ping por 90s -> watchdog deve cancelar a leitura e forçar reconexão.
        fakeTime.Advance(watchdogTimeout + TimeSpan.FromSeconds(1));
        await Task.Delay(100); // deixa a continuação do timer/cancelamento rodar.

        // depois do watchdog, o loop aplica o backoff normal antes de reconectar.
        fakeTime.Advance(TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => handler.RequestCount == 2, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Unauthorized_DoesNotReconnect()
    {
        var handler = new ScriptedSseHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test"), Timeout = Timeout.InfiniteTimeSpan };
        var fakeTime = new FakeTimeProvider();
        var client = new SseStreamClient(http, fakeTime);

        TokenInvalidReason? reason = null;
        client.TokenInvalidated += r => reason = r;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.RunAsync(cts.Token);

        Assert.Equal(TokenInvalidReason.Unauthorized, reason);

        // mesmo avançando bastante o relógio, não deve haver nova tentativa.
        fakeTime.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DeviceRevoked_SignalsAndStopsReconnecting()
    {
        var handler = new ScriptedSseHandler();
        handler.Enqueue(_ => StreamResponse(SseFrames.Concat(
            SseFrames.Frame("e0", "connected", """{"deviceId":"dev-1"}"""),
            SseFrames.Frame("e1", "device:revoked", """{"deviceId":"dev-1"}"""))));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test"), Timeout = Timeout.InfiniteTimeSpan };
        var fakeTime = new FakeTimeProvider();
        var client = new SseStreamClient(http, fakeTime);

        TokenInvalidReason? reason = null;
        client.TokenInvalidated += r => reason = r;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.RunAsync(cts.Token);

        Assert.Equal(TokenInvalidReason.DeviceRevoked, reason);

        fakeTime.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RateLimited_RespectsRetryAfterHeader()
    {
        var handler = new ScriptedSseHandler();
        handler.Enqueue(_ =>
        {
            var res = new HttpResponseMessage((HttpStatusCode)429);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return res;
        });
        handler.Enqueue(_ => StreamResponse(SseFrames.Frame("e0", "connected", """{"deviceId":"dev-1"}""")));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test"), Timeout = Timeout.InfiniteTimeSpan };
        var fakeTime = new FakeTimeProvider();
        var client = new SseStreamClient(http, fakeTime);

        using var cts = new CancellationTokenSource();
        var runTask = client.RunAsync(cts.Token);

        await WaitUntilAsync(() => handler.RequestCount == 1, TimeSpan.FromSeconds(5));

        // antes dos 5s do Retry-After, não deve ter reconectado ainda.
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(1, handler.RequestCount);

        fakeTime.Advance(TimeSpan.FromSeconds(4));
        await WaitUntilAsync(() => handler.RequestCount == 2, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await runTask;
    }
}
