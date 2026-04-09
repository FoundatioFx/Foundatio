using System;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Extensions;

public class TypeExtensionsTests
{
    private enum Color { Red, Green, Blue }

    private struct Point
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    #region Null handling

    [Fact]
    public void ToType_NullToReferenceType_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<string>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToType_NullToNullableInt_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<int?>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToType_NullToNullableLong_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<long?>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToType_NullToNullableDouble_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<double?>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToType_NullToNullableBool_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<bool?>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToType_NullToNullableEnum_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<Color?>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToType_NullToNullableStruct_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<Point?>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToType_NullToNullableGuid_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<Guid?>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToType_NullToNullableDateTime_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<DateTime?>();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToType_NullToInt_ThrowsArgumentNullException()
    {
        // Arrange
        object? value = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => value.ToType<int>());
    }

    [Fact]
    public void ToType_NullToLong_ThrowsArgumentNullException()
    {
        // Arrange
        object? value = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => value.ToType<long>());
    }

    [Fact]
    public void ToType_NullToDouble_ThrowsArgumentNullException()
    {
        // Arrange
        object? value = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => value.ToType<double>());
    }

    [Fact]
    public void ToType_NullToBool_ThrowsArgumentNullException()
    {
        // Arrange
        object? value = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => value.ToType<bool>());
    }

    [Fact]
    public void ToType_NullToEnum_ThrowsArgumentNullException()
    {
        // Arrange
        object? value = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => value.ToType<Color>());
    }

    [Fact]
    public void ToType_NullToStruct_ThrowsArgumentNullException()
    {
        // Arrange
        object? value = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => value.ToType<Point>());
    }

    [Fact]
    public void ToType_NullToGuid_ThrowsArgumentNullException()
    {
        // Arrange
        object? value = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => value.ToType<Guid>());
    }

    [Fact]
    public void ToType_NullToObject_ReturnsNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value.ToType<object>();

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Same type / assignable

    [Fact]
    public void ToType_SameReferenceType_ReturnsSameValue()
    {
        // Arrange
        var original = "hello";

        // Act
        var result = original.ToType<string>();

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ToType_SameValueType_ReturnsSameValue()
    {
        // Arrange
        var original = 42;

        // Act
        var result = original.ToType<int>();

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ToType_BoxedIntToObject_ReturnsSameValue()
    {
        // Arrange
        object value = 42;

        // Act
        var result = value.ToType<object>();

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ToType_DerivedToBase_ReturnsCast()
    {
        // Arrange
        var ex = new InvalidOperationException("test");

        // Act
        var result = ex.ToType<Exception>();

        // Assert
        Assert.Same(ex, result);
    }

    #endregion

    #region Numeric conversions

    [Fact]
    public void ToType_StringToInt_Converts()
    {
        // Arrange
        var value = "42";

        // Act
        var result = value.ToType<int>();

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ToType_StringToLong_Converts()
    {
        // Arrange
        var value = "9999999999";

        // Act
        var result = value.ToType<long>();

        // Assert
        Assert.Equal(9999999999L, result);
    }

    [Fact]
    public void ToType_StringToDouble_Converts()
    {
        // Arrange
        var value = "3.14";

        // Act
        var result = value.ToType<double>();

        // Assert
        Assert.Equal(3.14, result);
    }

    [Fact]
    public void ToType_StringToDecimal_Converts()
    {
        // Arrange
        var value = "99.99";

        // Act
        var result = value.ToType<decimal>();

        // Assert
        Assert.Equal(99.99m, result);
    }

    [Fact]
    public void ToType_IntToLong_Converts()
    {
        // Arrange
        var value = 42;

        // Act
        var result = value.ToType<long>();

        // Assert
        Assert.Equal(42L, result);
    }

    [Fact]
    public void ToType_IntToDouble_Converts()
    {
        // Arrange
        var value = 42;

        // Act
        var result = value.ToType<double>();

        // Assert
        Assert.Equal(42.0, result);
    }

    [Fact]
    public void ToType_IntToDecimal_Converts()
    {
        // Arrange
        var value = 42;

        // Act
        var result = value.ToType<decimal>();

        // Assert
        Assert.Equal(42m, result);
    }

    [Fact]
    public void ToType_LongToInt_Converts()
    {
        // Arrange
        var value = 42L;

        // Act
        var result = value.ToType<int>();

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ToType_DoubleToInt_Converts()
    {
        // Arrange
        var value = 42.0;

        // Act
        var result = value.ToType<int>();

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ToType_IntToNullableInt_Converts()
    {
        // Arrange
        var value = 42;

        // Act
        var result = value.ToType<int?>();

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ToType_StringToNullableInt_Converts()
    {
        // Arrange
        var value = "42";

        // Act
        var result = value.ToType<int?>();

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ToType_IntToByte_Converts()
    {
        // Arrange
        var value = 255;

        // Act
        var result = value.ToType<byte>();

        // Assert
        Assert.Equal((byte)255, result);
    }

    [Fact]
    public void ToType_StringToBool_Converts()
    {
        // Arrange
        var value = "true";

        // Act
        var result = value.ToType<bool>();

        // Assert
        Assert.True(result);
    }

    #endregion

    #region Enum conversions

    [Fact]
    public void ToType_StringToEnum_Converts()
    {
        // Arrange
        var value = "Green";

        // Act
        var result = value.ToType<Color>();

        // Assert
        Assert.Equal(Color.Green, result);
    }

    [Fact]
    public void ToType_NumericToEnum_Converts()
    {
        // Arrange
        var value = 2;

        // Act
        var result = value.ToType<Color>();

        // Assert
        Assert.Equal(Color.Blue, result);
    }

    [Fact]
    public void ToType_EnumToSameEnum_ReturnsSameValue()
    {
        // Arrange
        var value = Color.Red;

        // Act
        var result = value.ToType<Color>();

        // Assert
        Assert.Equal(Color.Red, result);
    }

    [Fact]
    public void ToType_StringToEnum_InvalidValue_ThrowsArgumentException()
    {
        // Arrange
        var value = "NotAColor";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => value.ToType<Color>());
    }

    [Fact]
    public void ToType_StringToNullableEnum_Converts()
    {
        // Arrange
        var value = "Blue";

        // Act
        var result = value.ToType<Color?>();

        // Assert
        Assert.Equal(Color.Blue, result);
    }

    #endregion

    #region Error cases

    [Fact]
    public void ToType_IncompatibleTypes_ThrowsArgumentException()
    {
        // Arrange
        var value = new object();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => value.ToType<int>());
    }

    [Fact]
    public void ToType_NonNumericStringToInt_ThrowsArgumentException()
    {
        // Arrange
        var value = "not-a-number";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => value.ToType<int>());
    }

    #endregion
}
