using System;
using System.IO;
using Utf8Json;
using Utf8Json.Resolvers;

namespace Foundatio.Serializer;

public class Utf8JsonSerializer : ITextSerializer
{
    private readonly IJsonFormatterResolver _formatterResolver;

    public Utf8JsonSerializer(IJsonFormatterResolver resolver = null)
    {
        _formatterResolver = resolver ?? StandardResolver.Default;
    }

    public void Serialize(object value, Stream output)
    {
        JsonSerializer.NonGeneric.Serialize(value.GetType(), output, value, _formatterResolver);
    }

    public object Deserialize(Stream data, Type objectType)
    {
        return JsonSerializer.NonGeneric.Deserialize(objectType, data, _formatterResolver);
    }
}
