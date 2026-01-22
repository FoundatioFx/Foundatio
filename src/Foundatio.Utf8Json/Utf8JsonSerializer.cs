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
        ArgumentNullException.ThrowIfNull(output);

        JsonSerializer.NonGeneric.Serialize(value?.GetType() ?? typeof(object), output, value, _formatterResolver);
    }

    public object Deserialize(Stream data, Type objectType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(objectType);

        return JsonSerializer.NonGeneric.Deserialize(objectType, data, _formatterResolver);
    }
}
