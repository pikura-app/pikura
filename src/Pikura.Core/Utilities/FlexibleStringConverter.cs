using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pikura.Core.Utilities;

/// <summary>
/// Pixiv's bookmarks endpoints (e.g. /ajax/user/{id}/illusts/bookmarks) sometimes serialize an
/// id-like field as a raw JSON number instead of a quoted string once you page deep enough into
/// the list (observed starting at offset=96). The default string converter throws on that token
/// type and aborts deserialization of the whole page, which silently stalls pagination. This
/// converter accepts either a JSON string or number token and normalizes it to a string.
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Cannot convert token type '{reader.TokenType}' to string.")
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
