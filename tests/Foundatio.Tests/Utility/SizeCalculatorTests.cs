using System;
using System.Collections.Generic;
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
        long size = _sizer.CalculateSize(value);
        Assert.True(size > 0, $"Expected size > 0 for boolean, got {size}");
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public void CalculateSize_WithByte_ReturnsExpectedSize(byte value)
    {
        long size = _sizer.CalculateSize(value);
        Assert.True(size > 0, $"Expected size > 0 for byte, got {size}");
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)-32768)]
    [InlineData((short)32767)]
    public void CalculateSize_WithInt16_ReturnsExpectedSize(short value)
    {
        long size = _sizer.CalculateSize(value);
        Assert.True(size >= 2, $"Expected size >= 2 for short, got {size}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void CalculateSize_WithInt32_ReturnsExpectedSize(int value)
    {
        long size = _sizer.CalculateSize(value);
        Assert.True(size >= 4, $"Expected size >= 4 for int, got {size}");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void CalculateSize_WithInt64_ReturnsExpectedSize(long value)
    {
        long size = _sizer.CalculateSize(value);
        Assert.True(size >= 8, $"Expected size >= 8 for long, got {size}");
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(float.MinValue)]
    [InlineData(float.MaxValue)]
    public void CalculateSize_WithFloat_ReturnsExpectedSize(float value)
    {
        long size = _sizer.CalculateSize(value);
        Assert.True(size >= 4, $"Expected size >= 4 for float, got {size}");
    }

    [Theory]
    [InlineData(0.0d)]
    [InlineData(double.MinValue)]
    [InlineData(double.MaxValue)]
    public void CalculateSize_WithDouble_ReturnsExpectedSize(double value)
    {
        long size = _sizer.CalculateSize(value);
        Assert.True(size >= 8, $"Expected size >= 8 for double, got {size}");
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
        long size = _sizer.CalculateSize(string.Empty);
        Assert.True(size >= 24, $"Expected size >= 24 for empty string (overhead), got {size}");
    }

    [Fact]
    public void CalculateSize_WithChar_ReturnsExpectedSize()
    {
        long size = _sizer.CalculateSize('a');
        Assert.True(size >= 2, $"Expected size >= 2 for char, got {size}");
    }

    [Fact]
    public void CalculateSize_WithDateTime_ReturnsExpectedSize()
    {
        long size = _sizer.CalculateSize(DateTime.UtcNow);
        Assert.True(size >= 8, $"Expected size >= 8 for DateTime, got {size}");
    }

    [Fact]
    public void CalculateSize_WithGuid_ReturnsExpectedSize()
    {
        long size = _sizer.CalculateSize(Guid.NewGuid());
        Assert.True(size >= 16, $"Expected size >= 16 for Guid, got {size}");
    }

    [Fact]
    public void CalculateSize_WithDecimal_ReturnsExpectedSize()
    {
        long size = _sizer.CalculateSize(123.456m);
        Assert.True(size >= 16, $"Expected size >= 16 for decimal, got {size}");
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
        var array = new[] { "hello", "world" };
        long size = _sizer.CalculateSize(array);
        // Array overhead + string sizes
        Assert.True(size > 24, $"Expected size > 24 for string array, got {size}");
        _logger.LogInformation("String array size: {Size}", size);
    }

    [Fact]
    public void CalculateSize_WithByteArray_ReturnsExpectedSize()
    {
        var array = new byte[100];
        long size = _sizer.CalculateSize(array);
        // Array overhead (24 bytes) + 100 bytes
        Assert.True(size >= 124, $"Expected size >= 124 for byte[100], got {size}");
    }

    [Fact]
    public void CalculateSize_WithEmptyList_ReturnsOverheadSize()
    {
        var list = new List<int>();
        long size = _sizer.CalculateSize(list);
        Assert.True(size > 0, $"Expected size > 0 for empty list, got {size}");
    }

    [Fact]
    public void CalculateSize_WithPopulatedList_IncludesElements()
    {
        var list = new List<int> { 1, 2, 3, 4, 5 };
        long size = _sizer.CalculateSize(list);
        var emptyListSize = _sizer.CalculateSize(new List<int>());
        Assert.True(size > emptyListSize, $"Expected size > {emptyListSize} for populated list, got {size}");
    }

    [Fact]
    public void CalculateSize_WithDictionary_ReturnsExpectedSize()
    {
        var dict = new Dictionary<string, int>
        {
            { "one", 1 },
            { "two", 2 },
            { "three", 3 }
        };
        long size = _sizer.CalculateSize(dict);
        Assert.True(size > 0, $"Expected size > 0 for dictionary, got {size}");
        _logger.LogInformation("Dictionary size: {Size}", size);
    }

    [Fact]
    public void CalculateSize_WithEmptyDictionary_ReturnsOverheadSize()
    {
        var dict = new Dictionary<string, string>();
        long size = _sizer.CalculateSize(dict);
        Assert.True(size > 0, $"Expected size > 0 for empty dictionary, got {size}");
    }

    [Fact]
    public void CalculateSize_WithHashSet_ReturnsExpectedSize()
    {
        var set = new HashSet<string> { "a", "b", "c" };
        long size = _sizer.CalculateSize(set);
        Assert.True(size > 0, $"Expected size > 0 for HashSet, got {size}");
    }

    [Fact]
    public void CalculateSize_WithNullableIntWithValue_ReturnsExpectedSize()
    {
        int? value = 42;
        long size = _sizer.CalculateSize(value);
        Assert.True(size >= 4, $"Expected size >= 4 for int?, got {size}");
    }

    [Fact]
    public void CalculateSize_WithNullableIntWithNull_ReturnsReferenceSize()
    {
        int? value = null;
        long size = _sizer.CalculateSize(value);
        Assert.Equal(8, size); // Null reference size
    }

    [Fact]
    public void CalculateSize_WithSimpleObject_ReturnsReasonableSize()
    {
        var obj = new SimpleTestObject { Id = 1, Name = "Test", Value = 123.45 };
        long size = _sizer.CalculateSize(obj);
        Assert.True(size > 0, $"Expected size > 0 for simple object, got {size}");
        _logger.LogInformation("SimpleTestObject size: {Size}", size);
    }

    [Fact]
    public void CalculateSize_WithNestedObject_IncludesNestedSize()
    {
        var inner = new SimpleTestObject { Id = 1, Name = "Inner", Value = 1.0 };
        var outer = new NestedTestObject { Id = 2, Inner = inner };

        long innerSize = _sizer.CalculateSize(inner);
        long outerSize = _sizer.CalculateSize(outer);

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
    public void CalculateSize_WithTimeSpan_ReturnsExpectedSize()
    {
        long size = _sizer.CalculateSize(TimeSpan.FromHours(1));
        Assert.True(size >= 8, $"Expected size >= 8 for TimeSpan, got {size}");
    }

    [Fact]
    public void CalculateSize_WithDateTimeOffset_ReturnsExpectedSize()
    {
        long size = _sizer.CalculateSize(DateTimeOffset.UtcNow);
        Assert.True(size >= 8, $"Expected size >= 8 for DateTimeOffset, got {size}");
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
        foreach (var obj in types)
        {
            long size = sizer.CalculateSize(obj);
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

        // The type cache is used for array element types
        // Each array type will cache its element type
        sizer.CalculateSize(new int[] { 1, 2, 3 });
        Assert.Equal(1, sizer.TypeCacheCount); // int type cached

        sizer.CalculateSize(new double[] { 1.0, 2.0 });
        Assert.Equal(2, sizer.TypeCacheCount); // double type cached

        sizer.CalculateSize(new bool[] { true, false });
        Assert.Equal(3, sizer.TypeCacheCount); // bool type cached

        sizer.CalculateSize(new DateTime[] { DateTime.Now });
        Assert.Equal(4, sizer.TypeCacheCount); // DateTime type cached

        sizer.CalculateSize(new Guid[] { Guid.NewGuid() });
        Assert.Equal(5, sizer.TypeCacheCount); // Guid type cached

        // Re-calculating the same array element types should NOT increase the cache count
        sizer.CalculateSize(new int[] { 100, 200 });
        sizer.CalculateSize(new double[] { 2.71 });
        sizer.CalculateSize(new bool[] { false });
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

    private class TypeCacheTestClass1
    {
        public int Value { get; set; }
    }

    private class TypeCacheTestClass2
    {
        public string Data { get; set; }
    }

    private class TypeCacheTestClass3
    {
        public bool Flag { get; set; }
    }

    private class TypeCacheTestClass4
    {
        public long Count { get; set; }
    }
}

