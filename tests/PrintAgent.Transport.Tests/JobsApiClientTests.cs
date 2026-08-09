using System.Net;
using System.Text.Json;
using PrintAgent.Contracts;

namespace PrintAgent.Transport.Tests;

public class JobsApiClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task AckJobAsync_SerializesPrintedAt_WithUtcZSuffix()
    {
        // Regressão: o conversor padrão de DateTimeOffset do System.Text.Json
        // escreve o offset explícito ("+00:00"), mas o backend valida
        // `printedAt` com z.iso.datetime() (Zod), que só aceita o sufixo "Z"
        // e rejeita offset — todo ack real voltava 400 Bad Request.
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test") };
        var client = new JobsApiClient(http);

        var printedAt = new DateTimeOffset(2026, 8, 9, 19, 41, 47, TimeSpan.Zero).AddTicks(2275009);
        var ack = new AckRequest { Status = AckRequestStatus.Printed, Attempts = 1, PrintedAt = printedAt };

        await client.AckJobAsync("job-1", ack);

        Assert.NotNull(handler.LastRequestBody);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var printedAtOnWire = doc.RootElement.GetProperty("printedAt").GetString();

        Assert.NotNull(printedAtOnWire);
        Assert.EndsWith("Z", printedAtOnWire);
        Assert.DoesNotContain("+00:00", printedAtOnWire);
    }
}
