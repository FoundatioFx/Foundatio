using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using Foundatio.Serializer;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Serializer;

public abstract class SerializerTestsBase : TestWithLoggingBase
{
    protected SerializerTestsBase(ITestOutputHelper output) : base(output)
    {
    }

    protected virtual ISerializer GetSerializer()
    {
        return null;
    }

    public virtual void Deserialize_WithInvalidInput_ThrowsArgumentException()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => serializer.Deserialize<SerializeModel>((Stream)null));
        Assert.Throws<ArgumentNullException>(() => serializer.Deserialize<SerializeModel>((byte[])null));
        Assert.Throws<ArgumentException>(() => serializer.Deserialize<SerializeModel>([]));
        Assert.Throws<ArgumentNullException>(() => serializer.Deserialize<SerializeModel>((string)null));
        Assert.Throws<ArgumentException>(() => serializer.Deserialize<SerializeModel>(String.Empty));
    }

    public virtual void Deserialize_WithPrimitiveType_ReturnsValue()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        object expected = "primitive";

        // Act
        string text = serializer.SerializeToString(expected);
        _logger.LogInformation(text);
        var actual = serializer.Deserialize<object>(text);

        // Assert
        Assert.Equal(expected, actual);
    }

    public virtual void Deserialize_WithUnicodeAndSpecialCharacters_PreservesContent()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        var model = new SerializeModel
        {
            IntProperty = 1,
            StringProperty = "Unicode: 中文 日本語 العربية Русский 🎉🚀💻 Special: \t\n\"\\",
            ListProperty = [1, 2, 3]
        };

        // Act
        string text = serializer.SerializeToString(model);
        var result = serializer.Deserialize<SerializeModel>(text);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(model.StringProperty, result.StringProperty);
    }

    public virtual void Deserialize_WithValidBytes_ReturnsDeserializedObject()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        var model = new SerializeModel
        {
            IntProperty = 1,
            StringProperty = "test",
            ListProperty = [1],
            ObjectProperty = new SerializeModel { IntProperty = 1 }
        };

        // Act
        byte[] bytes = serializer.SerializeToBytes(model);
        var actual = serializer.Deserialize<SerializeModel>(bytes);

        // Assert
        Assert.Equal(model.IntProperty, actual.IntProperty);
        Assert.Equal(model.StringProperty, actual.StringProperty);
        Assert.Equal(model.ListProperty, actual.ListProperty);

        // Act
        string text = serializer.SerializeToString(model);
        actual = serializer.Deserialize<SerializeModel>(text);

        // Assert
        Assert.Equal(model.IntProperty, actual.IntProperty);
        Assert.Equal(model.StringProperty, actual.StringProperty);
        Assert.Equal(model.ListProperty, actual.ListProperty);
        Assert.NotNull(model.ObjectProperty);
        Assert.Equal(1, ((dynamic)model.ObjectProperty).IntProperty);
    }

    public virtual void Deserialize_WithValidStream_ReturnsDeserializedObject()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        var model = new SerializeModel { IntProperty = 42, StringProperty = "test" };

        // Act
        using var stream = new MemoryStream();
        serializer.Serialize(model, stream);
        stream.Position = 0;
        var result = serializer.Deserialize<SerializeModel>(stream);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(model.IntProperty, result.IntProperty);
        Assert.Equal(model.StringProperty, result.StringProperty);
    }

    public virtual void Deserialize_WithValidString_ReturnsDeserializedObject()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        var model = new SerializeModel
        {
            IntProperty = 1,
            StringProperty = "test",
            ListProperty = [1],
            ObjectProperty = new SerializeModel { IntProperty = 1 }
        };

        // Act
        string text = serializer.SerializeToString(model);
        _logger.LogInformation(text);
        var actual = serializer.Deserialize<SerializeModel>(text);

        // Assert
        Assert.Equal(model.IntProperty, actual.IntProperty);
        Assert.Equal(model.StringProperty, actual.StringProperty);
        Assert.Equal(model.ListProperty, actual.ListProperty);
        Assert.NotNull(model.ObjectProperty);
        Assert.Equal(1, ((dynamic)model.ObjectProperty).IntProperty);
    }

    public virtual void SerializeToBytes_WithNullValue_ReturnsNull()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        // Act
        var result = serializer.SerializeToBytes<SerializeModel>(null);

        // Assert
        Assert.Null(result);
    }

    public virtual void SerializeToString_WithNullValue_ReturnsNull()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        // Act
        var result = serializer.SerializeToString<SerializeModel>(null);

        // Assert
        Assert.Null(result);
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public abstract class SerializerBenchmarkBase
{
    private ISerializer _serializer;
    private readonly SerializeModel _data = new()
    {
        IntProperty = 1,
        StringProperty = "test",
        ListProperty = [1],
        ObjectProperty = new SerializeModel { IntProperty = 1 }
    };

    private byte[] _serializedData;

    protected abstract ISerializer GetSerializer();

    [GlobalSetup]
    public void Setup()
    {
        _serializer = GetSerializer();
        _serializedData = _serializer.SerializeToBytes(_data);
    }

    [Benchmark]
    public byte[] Serialize()
    {
        return _serializer.SerializeToBytes(_data);
    }

    [Benchmark]
    public SerializeModel Deserialize()
    {
        return _serializer.Deserialize<SerializeModel>(_serializedData);
    }

    [Benchmark]
    public SerializeModel RoundTrip()
    {
        byte[] serializedData = _serializer.SerializeToBytes(_data);
        return _serializer.Deserialize<SerializeModel>(serializedData);
    }
}

public class SerializeModel
{
    public int IntProperty { get; set; }
    public string StringProperty { get; set; }
    public List<int> ListProperty { get; set; }
    public object ObjectProperty { get; set; }
}
