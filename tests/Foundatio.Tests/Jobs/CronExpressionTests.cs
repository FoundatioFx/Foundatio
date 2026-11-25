using System;
using System.Globalization;
using System.Linq;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Jobs.Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>
    /// The scheduler's binary search for the latest occurrence in a window finds what enumerating every occurrence
    /// in it finds, for schedules from every second to monthly, in UTC and in a zone with daylight saving time.
    /// </summary>
    [Theory]
    [InlineData("* * * * * *", true)]
    [InlineData("*/5 * * * * *", true)]
    [InlineData("7 */13 * * * *", true)]
    [InlineData("0 * * * *", false)]
    [InlineData("30 2 * * *", false)]
    [InlineData("0 9 * * MON-FRI", false)]
    [InlineData("0 0 1 * *", false)]
    public void GetLatestOccurrence_MatchesTheLastOccurrenceEnumeratedInTheWindow(string expression, bool includeSeconds)
    {
        var random = new Random(42);
        var chicago = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");
        foreach (var zone in new[] { TimeZoneInfo.Utc, chicago })
        {
            var schedule = new Foundatio.Jobs.CronSchedule("schedule", "job", CronExpression.Parse(expression, includeSeconds ? CronFormat.IncludeSeconds : CronFormat.Standard), null,
                new Foundatio.Jobs.CronJobOptions { TimeZone = zone });
            for (int i = 0; i < 200; i++)
            {
                var to = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(random.Next(0, 300 * 24 * 3600)).AddMilliseconds(random.Next(0, 1000));
                var from = to - TimeSpan.FromSeconds(random.Next(0, 3 * 24 * 3600));

                var expected = schedule.Expression.GetOccurrences(from, to, zone, fromInclusive: true, toInclusive: true).Cast<DateTimeOffset?>().LastOrDefault();

                Assert.Equal(expected, schedule.GetLatestOccurrence(from, to));
            }
        }
    }

    /// <summary>
    /// Samoa skipped 2011-12-30 by moving its base offset from -10:00 to +14:00, which .NET doesn't report as invalid
    /// times. Cronos then returns a time before the search's start for an occurrence on the skipped day, and enumerating
    /// from it never moves on; the scheduler's searches must still only move forward.
    /// </summary>
    [Theory]
    [InlineData("30 1 * * *", "2011-12-30T06:50:00Z", "2011-12-30T11:30:00Z")]
    [InlineData("0 * * * *", "2011-12-30T09:30:00Z", "2011-12-30T10:00:00Z")]
    [InlineData("*/15 * * * *", "2011-12-30T09:50:00Z", "2011-12-30T10:00:00Z")]
    [InlineData("30 1 * * *", "2011-12-29T10:00:00Z", "2011-12-29T11:30:00Z")]
    public void CronSchedule_GetNextOccurrence_AcrossADayAZoneSkipped_IsAfterTheStart(string expression, string from, string expected)
    {
        var cron = CronExpression.Parse(expression, CronFormat.Standard);
        var start = DateTimeOffset.Parse(from, CultureInfo.InvariantCulture);
        var schedule = new Foundatio.Jobs.CronSchedule("schedule", "job", cron, null, new Foundatio.Jobs.CronJobOptions { TimeZone = Samoa });

        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), schedule.GetNextOccurrence(start, inclusive: true));
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), schedule.GetNextOccurrence(start));
    }

    [Theory]
    [InlineData("30 1 * * *")]
    [InlineData("0 * * * *")]
    [InlineData("*/20 * * * *")]
    [InlineData("0 0 * * *")]
    public void CronSchedule_AroundADayAZoneSkipped_FindsOnlyOccurrencesInTheWindowAndMovesForward(string expression)
    {
        var cron = CronExpression.Parse(expression, CronFormat.Standard);
        var schedule = new Foundatio.Jobs.CronSchedule("schedule", "job", cron, null, new Foundatio.Jobs.CronJobOptions { TimeZone = Samoa });
        var skipped = new DateTimeOffset(2011, 12, 30, 10, 0, 0, TimeSpan.Zero);
        for (var to = skipped.AddHours(-24); to <= skipped.AddHours(6); to = to.AddMinutes(7))
        {
            var next = schedule.GetNextOccurrence(to, inclusive: true);
            Assert.True(next >= to, $"Next occurrence from {to:O} is {next:O}");

            foreach (var window in new[] { TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(3) })
            {
                var latest = schedule.GetLatestOccurrence(to - window, to);
                Assert.True(latest is null || latest >= to - window && latest <= to, $"Latest occurrence in [{to - window:O}, {to:O}] is {latest:O}");
            }

            var previous = schedule.GetPreviousOccurrence(to);
            Assert.True(previous < to, $"Previous occurrence before {to:O} is {previous:O}");
        }
    }

    [Fact]
    public void LegacyScheduledJob_AcrossADayAZoneSkipped_NextRunIsAfterNow()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2011, 12, 30, 6, 50, 0, TimeSpan.Zero));
        using var services = new ServiceCollection().AddSingleton<TimeProvider>(time).BuildServiceProvider();
        var manager = new JobManager(services, NullLoggerFactory.Instance);
        manager.AddOrUpdate("daily", b => b.CronTimeZone(Samoa).CronSchedule("0 0 1 1 *").JobAction(() => { }));
        manager.AddOrUpdate("daily", b => b.CronSchedule("30 1 * * *"));

        Assert.Equal(new DateTime(2011, 12, 30, 11, 30, 0, DateTimeKind.Utc), manager.GetJobStatus("daily").NextRun);
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
