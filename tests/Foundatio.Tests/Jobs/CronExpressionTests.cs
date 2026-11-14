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

    [Theory]
    [InlineData("@every_minute", false, "* * * * *")]
    [InlineData("@hourly", false, "0 * * * *")]
    [InlineData("@DAILY", false, "0 0 * * *")]
    [InlineData("  @midnight  ", false, "0 0 * * *")]
    [InlineData("@every_second", false, "* * * * * *")]
    [InlineData("*/15 9-17 ? JAN-mar,Jul MON-FRI", false, "*/15 9-17 * 1-3,7 1-5")]
    [InlineData("0 30 2 LW * *", true, "30 2 LW * *")]
    [InlineData("0 0 L-3 * *", false, "0 0 L-3 * *")]
    [InlineData("0 12 * * 5#3", false, "0 12 * * 5#3")]
    [InlineData("0 12 * * SUNL", false, "0 12 * * 0L")]
    [InlineData("0 12 15W * *", false, "0 12 15W * *")]
    [InlineData("5/20 1,3,5 * * *", false, "5-59/20 1,3,5 * * *")]
    public void Parse_ValidExpressions_MatchTheirEquivalents(string expression, bool includeSeconds, string equivalent)
    {
        var parsed = CronExpression.Parse(expression, includeSeconds ? CronFormat.IncludeSeconds : CronFormat.Standard);
        var expected = CronExpression.Parse(equivalent, equivalent.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);

        Assert.Equal(expected, parsed);
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected.GetOccurrences(from, from.AddDays(400), TimeZoneInfo.Utc).Take(50), parsed.GetOccurrences(from, from.AddDays(400), TimeZoneInfo.Utc).Take(50));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("* * * *", "Months: Unexpected character")]
    [InlineData("* * * * * *", "Unexpected character")]
    [InlineData("60 * * * *", "Minutes: Value must be a number between 0 and 59")]
    [InlineData("* 24 * * *", "Hours: Value must be a number between 0 and 23")]
    [InlineData("* * 32 * *", "Days of month: Value must be a number between 1 and 31")]
    [InlineData("* * * 13 *", "Months: Value must be a number between 1 and 12")]
    [InlineData("* * * FOO *", "Months: Value must be a number between 1 and 12")]
    [InlineData("* * * * 8", "Days of week: Value must be a number between 0 and 7")]
    [InlineData("*/0 * * * *", "Minutes: Value must be a number between 1 and 59")]
    [InlineData("@nope", "Macro")]
    [InlineData("@hourly extra", "Macro")]
    public void Parse_InvalidExpressions_Throw(string expression, string message)
    {
        if (expression.Length == 0)
        {
            Assert.Throws<ArgumentNullException>(() => CronExpression.Parse(expression, CronFormat.Standard));
            return;
        }

        var error = Assert.Throws<CronFormatException>(() => CronExpression.Parse(expression, CronFormat.Standard));
        Assert.Contains(message, error.Message);
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, "yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
}
