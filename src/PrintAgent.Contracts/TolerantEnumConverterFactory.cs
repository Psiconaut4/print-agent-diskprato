using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintAgent.Contracts;

/// <summary>
/// O contrato (v1.openapi.json, "Regras de compatibilidade") exige aceitar
/// valores de enum desconhecidos sem falhar, porque o backend pode
/// introduzir valores novos dentro da mesma major version. O
/// JsonStringEnumConverter embutido lanca JsonException nesse caso; este
/// factory absorve o erro e cai no membro "Unknown" do enum quando ele
/// existe (PrinterErrorCode, StatusReportPrinterState, etc.), ou no primeiro
/// membro declarado quando nao existe. patch-enum-handling.ps1 troca, a cada
/// geracao, o JsonStringEnumConverter padrao por este factory em todos os
/// enums gerados.
/// </summary>
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly JsonConverter<T> Inner =
            (JsonConverter<T>)new JsonStringEnumConverter().CreateConverter(typeof(T), new JsonSerializerOptions())!;

        private static readonly T FallbackValue = ResolveFallback();

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var checkpoint = reader;
            try
            {
                return Inner.Read(ref reader, typeToConvert, options);
            }
            catch (JsonException)
            {
                reader = checkpoint;
                reader.Skip();
                return FallbackValue;
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => Inner.Write(writer, value, options);

        private static T ResolveFallback()
        {
            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    return (T)Enum.Parse(typeof(T), name);
                }
            }

            return default;
        }
    }
}
