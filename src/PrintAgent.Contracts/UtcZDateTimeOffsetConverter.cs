using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintAgent.Contracts;

/// <summary>
/// O conversor padrao de <see cref="DateTimeOffset"/> do System.Text.Json
/// escreve o offset explicito ("+00:00") em vez do sufixo "Z", mesmo para
/// horarios em UTC. O backend valida datas recebidas com
/// <c>z.iso.datetime()</c> (Zod), que por padrao so aceita o sufixo "Z" e
/// rejeita offset explicito — um ack com <c>printedAt</c> serializado no
/// formato padrao do .NET sempre volta 400. Este conversor normaliza para
/// UTC e escreve sempre com "Z".
/// </summary>
public sealed class UtcZDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
}

public sealed class UtcZNullableDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly UtcZDateTimeOffsetConverter Inner = new();

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            Inner.Write(writer, v, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
