using System;
using System.IO;
using System.Text.Json;

namespace Foundatio.Serializer;

public class SystemTextJsonSerializer : ITextSerializer
{
    private readonly JsonSerializerOptions _defaultSerializeOptions = new();
    private readonly JsonSerializerOptions _defaultDeserializeOptions = new();

    private readonly JsonSerializerOptions _serializeOptions;
    private readonly JsonSerializerOptions _deserializeOptions;

    public SystemTextJsonSerializer(JsonSerializerOptions serializeOptions = null, JsonSerializerOptions deserializeOptions = null)
    {
        _serializeOptions = serializeOptions ?? _defaultSerializeOptions;
        _deserializeOptions = deserializeOptions ?? serializeOptions ?? _defaultDeserializeOptions;
    }

    public void Serialize(object value, Stream output)
    {
        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, value, value.GetType(), _serializeOptions);
        writer.Flush();
    }

    public object Deserialize(Stream data, Type objectType)
    {
        using var reader = new StreamReader(data);
        object result = JsonSerializer.Deserialize(reader.ReadToEnd(), objectType, _deserializeOptions);

        if (result is not JsonElement jsonElement)
            return result;

        // return primitive types
        switch (jsonElement.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return jsonElement.TryGetInt64(out long number) ? number : (object)jsonElement.GetDouble();
            case JsonValueKind.String:
                return jsonElement.TryGetDateTime(out var datetime) ? datetime : (object)jsonElement.GetString();
        }

        return result;
    }
}
