using System;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Extensions;

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
    public void SafeAdd_DateTimeOffset_WithPositiveOverflow_ReturnsMaxValue()
    {
        var date = DateTimeOffset.MaxValue.AddDays(-1);
        var result = date.SafeAdd(TimeSpan.FromDays(10));
        Assert.Equal(DateTimeOffset.MaxValue, result);
    }

    [Fact]
    public void SafeAdd_DateTimeOffset_WithNegativeOverflow_ReturnsMinValue()
    {
        var date = DateTimeOffset.MinValue.AddDays(1);
        var result = date.SafeAdd(TimeSpan.FromDays(-10));
        Assert.Equal(DateTimeOffset.MinValue, result);
    }

    [Fact]
    public void SafeAdd_DateTimeOffset_WithNormalValue_AddsCorrectly()
    {
        var date = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = date.SafeAdd(TimeSpan.FromDays(1));
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void SafeAddMilliseconds_WithPositiveOverflow_ReturnsMaxValue()
    {
        var date = DateTimeOffset.MaxValue.AddDays(-1);
        var result = date.SafeAddMilliseconds(Double.MaxValue);
        Assert.Equal(DateTimeOffset.MaxValue, result);
    }

    [Fact]
    public void SafeAddMilliseconds_WithNormalValue_AddsCorrectly()
    {
        var date = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = date.SafeAddMilliseconds(1000);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 1, TimeSpan.Zero), result);
    }

    [Fact]
    public void Floor_WithMinuteInterval_FloorsCorrectly()
    {
        var date = new DateTime(2020, 1, 1, 12, 34, 56, 789, DateTimeKind.Utc);
        var result = date.Floor(TimeSpan.FromMinutes(1));
        Assert.Equal(new DateTime(2020, 1, 1, 12, 34, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Floor_WithHourInterval_FloorsCorrectly()
    {
        var date = new DateTime(2020, 1, 1, 12, 34, 56, 789, DateTimeKind.Utc);
        var result = date.Floor(TimeSpan.FromHours(1));
        Assert.Equal(new DateTime(2020, 1, 1, 12, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Ceiling_WithMinuteInterval_CeilsCorrectly()
    {
        var date = new DateTime(2020, 1, 1, 12, 34, 56, 789, DateTimeKind.Utc);
        var result = date.Ceiling(TimeSpan.FromMinutes(1));
        Assert.Equal(new DateTime(2020, 1, 1, 12, 35, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ToUnixTimeMilliseconds_WithKnownDate_ReturnsCorrectValue()
    {
        var date = new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc);
        var result = date.ToUnixTimeMilliseconds();
        Assert.Equal(1000, result);
    }

    [Fact]
    public void FromUnixTimeMilliseconds_WithKnownValue_ReturnsCorrectDate()
    {
        long timestamp = 1000;
        var result = timestamp.FromUnixTimeMilliseconds();
        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ToUnixTimeSeconds_WithKnownDate_ReturnsCorrectValue()
    {
        var date = new DateTime(1970, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        var result = date.ToUnixTimeSeconds();
        Assert.Equal(60, result);
    }

    [Fact]
    public void FromUnixTimeSeconds_WithKnownValue_ReturnsCorrectDateTimeOffset()
    {
        long timestamp = 60;
        var result = timestamp.FromUnixTimeSeconds();
        Assert.Equal(new DateTimeOffset(1970, 1, 1, 0, 1, 0, TimeSpan.Zero), result);
    }
}
