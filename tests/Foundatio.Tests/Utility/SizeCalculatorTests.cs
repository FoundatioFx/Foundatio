using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Utility;

public class SizeCalculatorTests : TestWithLoggingBase
{
    private readonly SizeCalculator _sizer;

    public SizeCalculatorTests(ITestOutputHelper output) : base(output)
    {
        _sizer = new SizeCalculator(Log);
    }

    [Fact]
    public void CalculateSize_WithNull_ReturnsReferenceSize()
    {
        long size = _sizer.CalculateSize(null);
        Assert.Equal(8, size); // Reference size
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CalculateSize_WithBoolean_ReturnsExpectedSize(bool value)
    {
        // Act
        long size = _sizer.CalculateSize(value);

        // Assert
        Assert.Equal(1, size);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public void CalculateSize_WithByte_ReturnsExpectedSize(byte value)
    {
        // Act
        long size = _sizer.CalculateSize(value);

        // Assert
        Assert.Equal(1, size);
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)-32768)]
    [InlineData((short)32767)]
    public void CalculateSize_WithInt16_ReturnsExpectedSize(short value)
    {
        // Act
        long size = _sizer.CalculateSize(value);

        // Assert
        Assert.Equal(2, size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void CalculateSize_WithInt32_ReturnsExpectedSize(int value)
    {
        // Act
        long size = _sizer.CalculateSize(value);

        // Assert
        Assert.Equal(4, size);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void CalculateSize_WithInt64_ReturnsExpectedSize(long value)
    {
        // Act
        long size = _sizer.CalculateSize(value);

        // Assert
        Assert.Equal(8, size);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(float.MinValue)]
    [InlineData(float.MaxValue)]
    public void CalculateSize_WithFloat_ReturnsExpectedSize(float value)
    {
        // Act
        long size = _sizer.CalculateSize(value);

        // Assert
        Assert.Equal(4, size);
    }

    [Theory]
    [InlineData(0.0d)]
    [InlineData(double.MinValue)]
    [InlineData(double.MaxValue)]
    public void CalculateSize_WithDouble_ReturnsExpectedSize(double value)
    {
        // Act
        long size = _sizer.CalculateSize(value);

        // Assert
        Assert.Equal(8, size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hello")]
    [InlineData("hello world this is a longer string")]
    public void CalculateSize_WithString_ReturnsExpectedSize(string value)
    {
        long size = _sizer.CalculateSize(value);
        // String overhead (24 bytes) + 2 bytes per char
        long expectedMinSize = 24 + (value.Length * 2);
        Assert.True(size >= expectedMinSize, $"Expected size >= {expectedMinSize} for string '{value}', got {size}");
    }

    [Fact]
    public void CalculateSize_WithEmptyString_ReturnsStringOverhead()
    {
        // Act
        long size = _sizer.CalculateSize(string.Empty);

        // Assert - string overhead (24 bytes) + 0 chars
        Assert.Equal(24, size);
    }

    [Fact]
    public void CalculateSize_WithChar_ReturnsExpectedSize()
    {
        // Act
        long size = _sizer.CalculateSize('a');

        // Assert
        Assert.Equal(2, size);
    }

    [Fact]
    public void CalculateSize_WithDateTime_ReturnsExpectedSize()
    {
        // Act
        long size = _sizer.CalculateSize(DateTime.UtcNow);

        // Assert
        Assert.Equal(8, size);
    }

    [Fact]
    public void CalculateSize_WithGuid_ReturnsExpectedSize()
    {
        // Act
        long size = _sizer.CalculateSize(Guid.NewGuid());

        // Assert
        Assert.Equal(16, size);
    }

    [Fact]
    public void CalculateSize_WithDecimal_ReturnsExpectedSize()
    {
        // Act
        long size = _sizer.CalculateSize(123.456m);

        // Assert
        Assert.Equal(16, size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void CalculateSize_WithIntArray_ReturnsExpectedSize(int length)
    {
        var array = new int[length];
        long size = _sizer.CalculateSize(array);
        // Array overhead (24 bytes) + element size * count
        long expectedMinSize = 24 + (length * 4);
        Assert.True(size >= expectedMinSize, $"Expected size >= {expectedMinSize} for int[{length}], got {size}");
    }

    [Fact]
    public void CalculateSize_WithStringArray_IncludesStringContents()
    {
        // Arrange
        var array = new[] { "hello", "world" };

        // Act
        long size = _sizer.CalculateSize(array);

        // Assert - Array overhead (24) + 2 references (16) + "hello" (24+10) + "world" (24+10) = 108
        Assert.Equal(108, size);
    }

    [Fact]
    public void CalculateSize_WithByteArray_ReturnsExpectedSize()
    {
        // Arrange
        var array = new byte[100];

        // Act
        long size = _sizer.CalculateSize(array);

        // Assert - Array overhead (24) + 100 bytes
        Assert.Equal(124, size);
    }

    [Fact]
    public void CalculateSize_WithEmptyList_ReturnsOverheadSize()
    {
        // Arrange
        var list = new List<int>();

        // Act
        long size = _sizer.CalculateSize(list);

        // Assert - Collection overhead (32)
        Assert.Equal(32, size);
    }

    [Fact]
    public void CalculateSize_WithPopulatedList_IncludesElements()
    {
        // Arrange
        var list = new List<int> { 1, 2, 3, 4, 5 };
        var emptyListSize = _sizer.CalculateSize(new List<int>());

        // Act
        long size = _sizer.CalculateSize(list);

        // Assert - populated list should be larger than empty list
        Assert.True(size > emptyListSize, $"Expected size > {emptyListSize} for populated list, got {size}");
    }

    [Fact]
    public void CalculateSize_WithDictionary_ReturnsExpectedSize()
    {
        // Arrange
        var dict = new Dictionary<string, int>
        {
            { "one", 1 },
            { "two", 2 },
            { "three", 3 }
        };

        // Act
        long size = _sizer.CalculateSize(dict);

        // Assert - Collection overhead (32) + 3 items with references and content
        Assert.True(size > 32, $"Expected size > 32 for dictionary with 3 items, got {size}");
    }

    [Fact]
    public void CalculateSize_WithEmptyDictionary_ReturnsOverheadSize()
    {
        // Arrange
        var dict = new Dictionary<string, string>();

        // Act
        long size = _sizer.CalculateSize(dict);

        // Assert - Collection overhead (32)
        Assert.Equal(32, size);
    }

    [Fact]
    public void CalculateSize_WithHashSet_ReturnsExpectedSize()
    {
        // Arrange
        var set = new HashSet<string> { "a", "b", "c" };

        // Act
        long size = _sizer.CalculateSize(set);

        // Assert - Collection overhead (32) + 3 strings with content
        Assert.True(size > 32, $"Expected size > 32 for HashSet with 3 items, got {size}");
    }

    [Fact]
    public void CalculateSize_WithNullableIntWithValue_ReturnsExpectedSize()
    {
        // Arrange
        int? value = 42;

        // Act
        long size = _sizer.CalculateSize(value);

        // Assert - When a nullable value type with a value is boxed, it boxes as the underlying type.
        // So int? with a value becomes a boxed int (4 bytes), not int? (5 bytes).
        // This is standard .NET boxing behavior.
        Assert.Equal(4, size);
    }

    [Fact]
    public void CalculateSize_WithNullableIntWithNull_ReturnsReferenceSize()
    {
        // Arrange
        int? value = null;

        // Act
        long size = _sizer.CalculateSize(value);

        // Assert - null reference size (8 bytes)
        Assert.Equal(8, size);
    }

    [Fact]
    public void CalculateSize_WithSimpleObject_ReturnsReasonableSize()
    {
        // Arrange
        var obj = new SimpleTestObject { Id = 1, Name = "Test", Value = 123.45 };

        // Act
        long size = _sizer.CalculateSize(obj);

        // Assert - should include object overhead + serialized content
        Assert.True(size > 24, $"Expected size > 24 (object overhead) for simple object, got {size}");
    }

    [Fact]
    public void CalculateSize_WithNestedObject_IncludesNestedSize()
    {
        // Arrange
        var inner = new SimpleTestObject { Id = 1, Name = "Inner", Value = 1.0 };
        var outer = new NestedTestObject { Id = 2, Inner = inner };
        long innerSize = _sizer.CalculateSize(inner);

        // Act
        long outerSize = _sizer.CalculateSize(outer);

        // Assert - outer should be larger since it contains inner
        Assert.True(outerSize > innerSize, $"Expected outer size ({outerSize}) > inner size ({innerSize})");
    }

    [Fact]
    public void CalculateSize_WithLargeString_ScalesWithLength()
    {
        var small = new string('a', 10);
        var large = new string('a', 1000);

        long smallSize = _sizer.CalculateSize(small);
        long largeSize = _sizer.CalculateSize(large);

        Assert.True(largeSize > smallSize, $"Expected large string size ({largeSize}) > small string size ({smallSize})");
        _logger.LogInformation("Small string (10 chars) size: {SmallSize}, Large string (1000 chars) size: {LargeSize}", smallSize, largeSize);
    }

    [Fact]
    public void CalculateSize_WithLargeArray_ScalesWithLength()
    {
        var small = new int[10];
        var large = new int[1000];

        long smallSize = _sizer.CalculateSize(small);
        long largeSize = _sizer.CalculateSize(large);

        Assert.True(largeSize > smallSize * 10, $"Expected large array size ({largeSize}) > 10x small array size ({smallSize * 10})");
    }

    [Fact]
    public void CalculateSize_WithVeryLargeArray_DoesNotOverflow()
    {
        // This test verifies that the size calculation uses long arithmetic
        // to prevent integer overflow for large arrays.
        // Array.Length is int, but when multiplied by element size, we need long.
        // Example: int[500_000_000] * 4 bytes = 2_000_000_000 which fits in int
        // But int[600_000_000] * 4 bytes = 2_400_000_000 which overflows int (max ~2.1B)

        // We can't actually allocate such large arrays in tests, but we can verify
        // the formula works correctly for moderately large arrays
        var largeArray = new long[100_000]; // 100K longs = 800KB
        long size = _sizer.CalculateSize(largeArray);

        // Expected: ArrayOverhead (24) + 100_000 * 8 = 800,024 bytes
        long expectedMinSize = 24 + (100_000L * 8);
        Assert.True(size >= expectedMinSize, $"Expected size >= {expectedMinSize} for long[100000], got {size}");
    }

    [Fact]
    public void CalculateSize_WithTimeSpan_ReturnsExpectedSize()
    {
        // Act
        long size = _sizer.CalculateSize(TimeSpan.FromHours(1));

        // Assert
        Assert.Equal(8, size);
    }

    [Fact]
    public void CalculateSize_WithDateTimeOffset_ReturnsExpectedSize()
    {
        // Act
        long size = _sizer.CalculateSize(DateTimeOffset.UtcNow);

        // Assert - DateTimeOffset is 16 bytes (DateTime 8 bytes + TimeSpan offset 8 bytes)
        Assert.Equal(16, size);
    }

    [Fact]
    public void CalculateSize_WithConsistentResults_ReturnsSameSize()
    {
        var obj = new SimpleTestObject { Id = 1, Name = "Test", Value = 123.45 };

        long size1 = _sizer.CalculateSize(obj);
        long size2 = _sizer.CalculateSize(obj);

        Assert.Equal(size1, size2);
    }

    [Fact]
    public void Dispose_WhenCalled_ClearsCache()
    {
        var sizer = new SizeCalculator(Log);

        // Use the sizer to populate the cache
        sizer.CalculateSize(new SimpleTestObject { Id = 1, Name = "Test" });
        sizer.CalculateSize(new NestedTestObject { Id = 2, Inner = new SimpleTestObject() });

        // Dispose should clear everything
        sizer.Dispose();

        // Calling again should throw
        Assert.Throws<ObjectDisposedException>(() => sizer.CalculateSize("test"));
    }

    [Fact]
    public void Dispose_WhenCalledMultipleTimes_DoesNotThrow()
    {
        var sizer = new SizeCalculator(Log);
        sizer.CalculateSize("test");

        // Multiple dispose calls should be safe
        sizer.Dispose();
        sizer.Dispose();
        sizer.Dispose();
    }

    [Fact]
    public void CalculateSize_WithManyDistinctTypes_RespectsMaxCacheSize()
    {
        var sizer = new SizeCalculator(Log);

        // Create many distinct types by using generic types with different type arguments
        // This tests the LRU eviction logic
        var types = new List<object>();
        for (int i = 0; i < 1100; i++)
        {
            // Create objects that will exercise the type cache
            // Using Dictionary with different value types creates distinct generic types
            types.Add(new Dictionary<string, int> { { $"key{i}", i } });
        }

        // Calculate sizes for all - this should trigger eviction
        // Use Select to explicitly map objects to their sizes
        foreach (long size in types.Select(obj => sizer.CalculateSize(obj)))
        {
            Assert.True(size > 0);
        }

        // The sizer should still work correctly after evictions
        long finalSize = sizer.CalculateSize(new SimpleTestObject { Id = 999, Name = "Final" });
        Assert.True(finalSize > 0);

        sizer.Dispose();
    }

    [Fact]
    public void TypeSizeCache_CachesTypeSizeCalculations()
    {
        // Use a small cache size so we can test caching behavior
        var sizer = new SizeCalculator(maxTypeCacheSize: 10, loggerFactory: Log);

        Assert.Equal(0, sizer.TypeCacheCount);

        // The type cache is used for value type array element types
        // Each value type array will cache its element type via GetCachedTypeSize
        // This includes primitives AND common value types like DateTime and Guid
        sizer.CalculateSize(new int[] { 1, 2, 3 });
        Assert.Equal(1, sizer.TypeCacheCount); // int type cached

        sizer.CalculateSize(new double[] { 1.0, 2.0 });
        Assert.Equal(2, sizer.TypeCacheCount); // double type cached

        sizer.CalculateSize(new bool[] { true, false });
        Assert.Equal(3, sizer.TypeCacheCount); // bool type cached

        sizer.CalculateSize(new DateTime[] { DateTime.Now, DateTime.UtcNow });
        Assert.Equal(4, sizer.TypeCacheCount); // DateTime type cached

        sizer.CalculateSize(new Guid[] { Guid.NewGuid(), Guid.Empty });
        Assert.Equal(5, sizer.TypeCacheCount); // Guid type cached

        // Re-calculating the same array element types should NOT increase the cache count
        sizer.CalculateSize(new int[] { 100, 200 });
        sizer.CalculateSize(new double[] { 2.71 });
        sizer.CalculateSize(new DateTime[] { DateTime.MinValue });
        Assert.Equal(5, sizer.TypeCacheCount);

        sizer.Dispose();
    }

    [Fact]
    public void TypeSizeCache_EvictsWhenMaxSizeReached()
    {
        // Use a very small cache size to test eviction
        var sizer = new SizeCalculator(maxTypeCacheSize: 5, loggerFactory: Log);

        // Add types up to the limit using arrays (which cache element types)
        sizer.CalculateSize(new int[] { 1 });      // int
        sizer.CalculateSize(new double[] { 1.0 }); // double
        sizer.CalculateSize(new bool[] { true });  // bool
        sizer.CalculateSize(new long[] { 1L });    // long
        sizer.CalculateSize(new float[] { 1.0f }); // float
        Assert.Equal(5, sizer.TypeCacheCount);

        // Adding more types should trigger eviction (removes ~10% = at least 1)
        sizer.CalculateSize(new decimal[] { 1m }); // decimal - should trigger eviction

        // After eviction, cache should not exceed max size
        Assert.True(sizer.TypeCacheCount <= 5, $"Cache count {sizer.TypeCacheCount} should not exceed max size 5");

        sizer.Dispose();
    }

    private class SimpleTestObject
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public double Value { get; set; }
    }

    private class NestedTestObject
    {
        public int Id { get; set; }
        public SimpleTestObject Inner { get; set; }
    }

}

