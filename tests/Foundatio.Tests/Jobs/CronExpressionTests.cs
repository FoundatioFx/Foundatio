using System;
using System.Globalization;
using System.Linq;
using Foundatio.Jobs.Cronos;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class CronExpressionTests
{
    private static readonly TimeZoneInfo Samoa = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Samoa Standard Time" : "Pacific/Apia");

    /// <summary>
    /// Samoa's base offset is +13:00 today, but on 2011-04-02 its clocks fell back from -10:00 to -11:00. The repeated
    /// hour's offsets must come from that date, not from today's base offset (upstream Cronos e49df37).
    /// </summary>
    [Theory]
    [InlineData("30 3 * * *", "2011-04-02 03:00 -10:00", "2011-04-02 03:30 -10:00")]
    [InlineData("30 3 * * *", "2011-04-02 03:30 -11:00", "2011-04-03 03:30 -11:00")]
    [InlineData("*/30 * * * *", "2011-04-02 03:31 -10:00", "2011-04-02 03:00 -11:00")]
    [InlineData("*/30 * * * *", "2011-04-02 03:01 -11:00", "2011-04-02 03:30 -11:00")]
    public void GetNextOccurrence_InTheRepeatedHourOfAZoneWhoseBaseOffsetLaterChanged_UsesThatDatesOffsets(string expression, string from, string expected)
    {
        var repeated = new DateTime(2011, 4, 2, 3, 30, 0);
        Assert.SkipUnless(Samoa.IsAmbiguousTime(repeated) && !Samoa.GetAmbiguousTimeOffsets(repeated).Contains(Samoa.BaseUtcOffset),
            "This platform's time zone data does not record Samoa's 2011 fall-back as a repeated hour.");

        var next = CronExpression.Parse(expression, CronFormat.Standard).GetNextOccurrence(Parse(from), Samoa, inclusive: true);

        Assert.Equal(Parse(expected), next);
        Assert.Equal(Parse(expected).Offset, next?.Offset);
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, "yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
}
