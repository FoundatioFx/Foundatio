﻿using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foundatio.Serializer;

public class SystemTextJsonSerializer : ITextSerializer
{
    private readonly JsonSerializerOptions _defaultSerializeOptions = new();
    private readonly JsonSerializerOptions _defaultDeserializeOptions = new() { Converters = { new ObjectToInferredTypesConverter() }};

    private readonly JsonSerializerOptions _serializeOptions;
    private readonly JsonSerializerOptions _deserializeOptions;

    public SystemTextJsonSerializer(JsonSerializerOptions serializeOptions = null, JsonSerializerOptions deserializeOptions = null)
    {
        _serializeOptions = serializeOptions ?? _defaultSerializeOptions;
        _deserializeOptions = deserializeOptions ?? serializeOptions ?? _defaultDeserializeOptions;
    }

    public void Serialize(object data, Stream outputStream)
    {
        var writer = new Utf8JsonWriter(outputStream);
        JsonSerializer.Serialize(writer, data, data.GetType(), _serializeOptions);
        writer.Flush();
    }

    public object Deserialize(Stream inputStream, Type objectType)
    {
        using var reader = new StreamReader(inputStream);
        return JsonSerializer.Deserialize(reader.ReadToEnd(), objectType, _deserializeOptions);
    }
}

public class ObjectToInferredTypesConverter : JsonConverter<object>
{
    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.True)
            return true;

        if (reader.TokenType == JsonTokenType.False)
            return false;

        if (reader.TokenType == JsonTokenType.Number)
            return reader.TryGetInt64(out long number) ? number : (object)reader.GetDouble();

        if (reader.TokenType == JsonTokenType.String)
            return reader.TryGetDateTime(out var datetime) ? datetime : (object)reader.GetString();

        using var document = JsonDocument.ParseValue(ref reader);

        return document.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, object objectToWrite, JsonSerializerOptions options)
    {
        throw new InvalidOperationException();
    }
}
