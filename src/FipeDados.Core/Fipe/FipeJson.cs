using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FipeDados.Core.Fipe;

/// <summary>Opções de JSON compartilhadas para (de)serializar as respostas da Fipe.</summary>
public static class FipeJson
{
    public static readonly JsonSerializerOptions Options = Criar();

    private static JsonSerializerOptions Criar()
    {
        var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        o.Converters.Add(new FlexibleStringConverter());
        return o;
    }
}

/// <summary>
/// Normaliza para string um campo que a Fipe envia ora como string, ora como número.
/// Ex.: o "Value" de marcas vem como "21" (string), mas o de modelos vem como 4585 (número).
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => reader.GetString()
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
