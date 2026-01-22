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
        Assert.Throws<ArgumentException>(() => serializer.Deserialize<SerializeModel>("   "));
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

    public virtual void Deserialize_WithInvalidArguments_ThrowsArgumentNullException()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        using var stream = new MemoryStream([0x7B, 0x7D]); // "{}"

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => serializer.Deserialize(null, typeof(object)));
        Assert.Throws<ArgumentNullException>(() => serializer.Deserialize(stream, null));
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

    public virtual void Serialize_WithInvalidArguments_ThrowsArgumentNullException()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        // Act & Assert - only stream is validated (null value is allowed)
        Assert.Throws<ArgumentNullException>(() => serializer.Serialize(new object(), null));
    }

    public virtual void Serialize_WithNullValue_RoundTripsCorrectly()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        // Act & Assert - extension methods produce valid output (not null)
        var bytes = serializer.SerializeToBytes<object>(null);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        var str = serializer.SerializeToString<object>(null);
        Assert.NotNull(str);
        Assert.NotEmpty(str);

        // Act & Assert - round trip via extension methods
        var resultFromBytes = serializer.Deserialize<object>(bytes);
        Assert.Null(resultFromBytes);

        var resultFromString = serializer.Deserialize<object>(str);
        Assert.Null(resultFromString);

        // Act & Assert - round trip via core Serialize method
        using var stream = new MemoryStream();
        serializer.Serialize(null, stream);
        Assert.True(stream.Length > 0);
        stream.Position = 0;
        var resultFromStream = serializer.Deserialize<object>(stream);
        Assert.Null(resultFromStream);
    }

    public virtual void Serialize_WithSpecialCharacters_RoundTripsCorrectly()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        var model = new SerializeModel
        {
            IntProperty = 42,
            StringProperty = "Unicode: 中文 日本語 العربية Русский 🎉🚀💻 " +
                             "Escapes: line1\nline2\r\nwindows\ttab\"quote\\backslash " +
                             "Surrogate: 𝄞🎵 " +
                             "Null: before\0after",
            ListProperty = [1, 2, 3]
        };

        // Act - round trip via bytes
        var bytes = serializer.SerializeToBytes(model);
        var resultFromBytes = serializer.Deserialize<SerializeModel>(bytes);

        // Act - round trip via string
        var str = serializer.SerializeToString(model);
        var resultFromString = serializer.Deserialize<SerializeModel>(str);

        // Assert - semantic equality (not comparing serialized output)
        Assert.NotNull(resultFromBytes);
        Assert.Equal(model.IntProperty, resultFromBytes.IntProperty);
        Assert.Equal(model.StringProperty, resultFromBytes.StringProperty);
        Assert.Equal(model.ListProperty, resultFromBytes.ListProperty);

        Assert.NotNull(resultFromString);
        Assert.Equal(model.IntProperty, resultFromString.IntProperty);
        Assert.Equal(model.StringProperty, resultFromString.StringProperty);
        Assert.Equal(model.ListProperty, resultFromString.ListProperty);
    }

    public virtual void Serialize_WithEmptyCollection_ReturnsValidOutput()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        var model = new SerializeModel
        {
            IntProperty = 1,
            StringProperty = "test",
            ListProperty = []
        };

        // Act
        var bytes = serializer.SerializeToBytes(model);
        var result = serializer.Deserialize<SerializeModel>(bytes);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.ListProperty);
        Assert.Empty(result.ListProperty);
    }

    public virtual void Serialize_WithNullPropertyInObject_HandlesCorrectly()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        var model = new SerializeModel
        {
            IntProperty = 1,
            StringProperty = null,
            ListProperty = null,
            ObjectProperty = null
        };

        // Act
        var bytes = serializer.SerializeToBytes(model);
        var result = serializer.Deserialize<SerializeModel>(bytes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.IntProperty);
        Assert.Null(result.StringProperty);
        Assert.Null(result.ListProperty);
        Assert.Null(result.ObjectProperty);
    }

    public virtual void Serialize_WithDateTimeValue_PreservesValue()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        var model = new SerializeModelWithDateTime
        {
            DateTimeProperty = new DateTime(2024, 6, 15, 12, 30, 45, DateTimeKind.Utc),
            DateTimeOffsetProperty = new DateTimeOffset(2024, 6, 15, 12, 30, 45, TimeSpan.FromHours(-5))
        };

        // Act
        var bytes = serializer.SerializeToBytes(model);
        var result = serializer.Deserialize<SerializeModelWithDateTime>(bytes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(model.DateTimeProperty, result.DateTimeProperty);
        Assert.Equal(model.DateTimeOffsetProperty, result.DateTimeOffsetProperty);
    }

    public virtual void Serialize_WithNumericTypes_PreservesValues()
    {
        // Arrange
        var serializer = GetSerializer();
        if (serializer is null)
            return;

        var model = new SerializeModelWithNumerics
        {
            IntValue = 42,
            LongValue = 9_223_372_036_854_775_807L,
            DoubleValue = 3.14159265358979,
            DecimalValue = 123456.789m,
            BoolValue = true
        };

        // Act
        var bytes = serializer.SerializeToBytes(model);
        var result = serializer.Deserialize<SerializeModelWithNumerics>(bytes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(model.IntValue, result.IntValue);
        Assert.Equal(model.LongValue, result.LongValue);
        Assert.Equal(model.DoubleValue, result.DoubleValue, 10);
        Assert.Equal(model.DecimalValue, result.DecimalValue);
        Assert.Equal(model.BoolValue, result.BoolValue);
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

public class SerializeModelWithDateTime
{
    public DateTime DateTimeProperty { get; set; }
    public DateTimeOffset DateTimeOffsetProperty { get; set; }
}

public class SerializeModelWithNumerics
{
    public int IntValue { get; set; }
    public long LongValue { get; set; }
    public double DoubleValue { get; set; }
    public decimal DecimalValue { get; set; }
    public bool BoolValue { get; set; }
}
