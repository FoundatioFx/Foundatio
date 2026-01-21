using System;
using System.IO;
using Newtonsoft.Json;

namespace Foundatio.Serializer;

public class JsonNetSerializer : ITextSerializer
{
    private readonly JsonSerializer _serializer;

    public JsonNetSerializer(JsonSerializerSettings settings = null)
    {
        _serializer = JsonSerializer.Create(settings ?? new JsonSerializerSettings());
    }

    public void Serialize(object value, Stream output)
    {
        using var streamWriter = new StreamWriter(output, leaveOpen: true);
        using var writer = new JsonTextWriter(streamWriter);
        _serializer.Serialize(writer, value, value.GetType());
        writer.Flush();
    }

    public object Deserialize(Stream data, Type objectType)
    {
        using var sr = new StreamReader(data);
        using var reader = new JsonTextReader(sr);
        return _serializer.Deserialize(reader, objectType);
    }
}
