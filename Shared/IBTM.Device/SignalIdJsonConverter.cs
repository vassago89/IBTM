using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IBTM.Device;

// Numeric IDs are the storage contract. Names are accepted only for existing settings.
public sealed class SignalIdJsonConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    private static readonly JsonConverter<T> Names = (JsonConverter<T>)
            new JsonStringEnumConverter<T>().CreateConverter(typeof(T), JsonSerializerOptions.Default);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String
            && Enum.TryParse<T>(reader.GetString(), out var named)
            ? named
            : Names.Read(ref reader, typeToConvert, options);
        return RequireDefined(value);
    }

    public override T ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = Enum.TryParse<T>(reader.GetString(), out var named)
            ? named
            : Names.ReadAsPropertyName(ref reader, typeToConvert, options);
        return RequireDefined(value);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(Convert.ToInt32(RequireDefined(value)));
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(Convert.ToInt32(RequireDefined(value)).ToString(CultureInfo.InvariantCulture));
    }

    private static T RequireDefined(T value)
    {
        if (!Enum.IsDefined(value))
            throw new JsonException($"Unknown {typeof(T).Name} ID: {value}.");
        return value;
    }
}
