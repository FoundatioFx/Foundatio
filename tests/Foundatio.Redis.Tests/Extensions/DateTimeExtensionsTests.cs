using System;
using Foundatio.Redis.Extensions;
using Xunit;

namespace Foundatio.Redis.Tests.Extensions;

public class DateTimeExtensionsTests
{
    [Fact]
    public void SafeAdd_WithPositiveOverflow_ReturnsMaxValue()
    {
        var date = DateTime.MaxValue.AddDays(-1);
        var result = date.SafeAdd(TimeSpan.FromDays(10));
        Assert.Equal(DateTime.MaxValue, result);
    }

    [Fact]
    public void SafeAdd_WithNegativeOverflow_ReturnsMinValue()
    {
        var date = DateTime.MinValue.AddDays(1);
        var result = date.SafeAdd(TimeSpan.FromDays(-10));
        Assert.Equal(DateTime.MinValue, result);
    }

    [Fact]
    public void SafeAdd_WithNormalPositiveValue_AddsCorrectly()
    {
        var date = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = date.SafeAdd(TimeSpan.FromDays(1));
        Assert.Equal(new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void SafeAdd_WithNormalNegativeValue_SubtractsCorrectly()
    {
        var date = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var result = date.SafeAdd(TimeSpan.FromDays(-1));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void SafeAdd_WithZero_ReturnsSameValue()
    {
        var date = new DateTime(2020, 6, 15, 12, 30, 45, DateTimeKind.Utc);
        var result = date.SafeAdd(TimeSpan.Zero);
        Assert.Equal(date, result);
    }

    [Fact]
    public void SafeAdd_WithMaxValueDate_HandlesPositiveTimeSpan()
    {
        var result = DateTime.MaxValue.SafeAdd(TimeSpan.FromTicks(1));
        Assert.Equal(DateTime.MaxValue, result);
    }

    [Fact]
    public void SafeAdd_WithMinValueDate_HandlesNegativeTimeSpan()
    {
        var result = DateTime.MinValue.SafeAdd(TimeSpan.FromTicks(-1));
        Assert.Equal(DateTime.MinValue, result);
    }

    [Fact]
    public void ToUnixTimeMilliseconds_WithKnownDate_ReturnsCorrectValue()
    {
        var date = new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc);
        var result = date.ToUnixTimeMilliseconds();
        Assert.Equal(1000, result);
    }

    [Fact]
    public void ToUnixTimeMilliseconds_WithEpoch_ReturnsZero()
    {
        var date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = date.ToUnixTimeMilliseconds();
        Assert.Equal(0, result);
    }

    [Fact]
    public void FromUnixTimeMilliseconds_WithKnownValue_ReturnsCorrectDate()
    {
        long timestamp = 1000;
        var result = timestamp.FromUnixTimeMilliseconds();
        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc), result);
    }

    [Fact]
    public void FromUnixTimeMilliseconds_WithZero_ReturnsEpoch()
    {
        long timestamp = 0;
        var result = timestamp.FromUnixTimeMilliseconds();
        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ToUnixTimeMilliseconds_RoundTrip_PreservesValue()
    {
        var original = new DateTime(2024, 6, 15, 12, 30, 45, 123, DateTimeKind.Utc);
        var timestamp = original.ToUnixTimeMilliseconds();
        var roundTripped = timestamp.FromUnixTimeMilliseconds();
        Assert.Equal(original, roundTripped);
    }
}

