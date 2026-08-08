using System.Text;

namespace PrintAgent.Transport.Tests.Sse;

/// <summary>Monta corpos de resposta SSE no mesmo formato que o cliente sabe ler (§6.3).</summary>
internal static class SseFrames
{
    public static string Frame(string? id, string evt, string data)
    {
        var sb = new StringBuilder();
        if (id is not null) sb.Append("id: ").Append(id).Append('\n');
        sb.Append("event: ").Append(evt).Append('\n');
        sb.Append("data: ").Append(data).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    public static string Concat(params string[] frames) => string.Concat(frames);
}
