using System;
using System.Linq;
using Foundatio.Monitoring;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Monitoring;

public class WorkTrackerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 30, TimeSpan.Zero));
    private readonly WorkTracker _tracker;

    public WorkTrackerTests()
    {
        _tracker = new WorkTracker(_time, TimeSpan.FromSeconds(1), recentCapacity: 3, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void GetSnapshot_AfterFastWorkSucceeds_CountsItWithoutListingIt()
    {
        var counter = _tracker.GetCounter(WorkKind.Message, "orders.placed", "queue:orders");
        var work = new Work("m1");

        _tracker.Start(work, counter, "OrderPlacedHandler");
        _time.Advance(TimeSpan.FromMilliseconds(40));
        _tracker.Complete(work, WorkOutcome.Succeeded, null);

        var snapshot = _tracker.GetSnapshot();
        Assert.Empty(snapshot.Active);
        Assert.Empty(snapshot.Recent);
        var stats = Assert.Single(snapshot.Stats);
        Assert.Equal((WorkKind.Message, "orders.placed", "queue:orders"), (stats.Kind, stats.Name, stats.Source));
        Assert.Equal(1, stats.Succeeded);
        Assert.Equal(1, stats.Timed);
        Assert.Equal(TimeSpan.FromMilliseconds(40), stats.TotalDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(40), stats.MaxDuration);
        Assert.Equal(0, stats.InFlight);
    }

    [Fact]
    public void GetSnapshot_WhileWorkRunsPastTheThreshold_ListsItAsActive()
    {
        var counter = _tracker.GetCounter(WorkKind.Job, "reports.generate", null);
        var work = new Work("job-1", attempt: 2, correlationId: "order-7");
        var startedAt = _time.GetUtcNow();
        _tracker.Start(work, counter, "GenerateReportJob");

        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Empty(_tracker.GetSnapshot().Active);
        Assert.Equal(1, Assert.Single(_tracker.GetSnapshot().Stats).InFlight);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        var active = Assert.Single(_tracker.GetSnapshot().Active);
        Assert.Equal(WorkKind.Job, active.Kind);
        Assert.Equal("job-1", active.Id);
        Assert.Equal("reports.generate", active.Name);
        Assert.Null(active.Source);
        Assert.Equal("GenerateReportJob", active.Handler);
        Assert.Equal(2, active.Attempt);
        Assert.Equal("order-7", active.CorrelationId);
        Assert.Equal(startedAt, active.StartedAt);
        Assert.Null(active.Progress);
        Assert.Null(active.ProgressAt);
    }

    [Fact]
    public void GetSnapshot_WhenWorkReportsProgress_ListsItBeforeTheThreshold()
    {
        var counter = _tracker.GetCounter(WorkKind.Message, "catalog.import", "queue:catalog.import");
        var work = new Work("m1");
        _tracker.Start(work, counter, "ImportHandler");
        long version = _tracker.Version;

        _time.Advance(TimeSpan.FromMilliseconds(100));
        work.Report(30, "reading");
        work.Report(null, null);
        work.Report(45, null);

        var active = Assert.Single(_tracker.GetSnapshot().Active);
        Assert.Equal(45, active.Progress);
        Assert.Equal("reading", active.ProgressMessage);
        Assert.Equal(_time.GetUtcNow(), active.ProgressAt);
        Assert.NotEqual(version, _tracker.Version);
    }

    [Fact]
    public void Complete_WhenFastWorkFails_KeepsItInRecent()
    {
        var counter = _tracker.GetCounter(WorkKind.Message, "orders.send-receipt", "queue:orders.send-receipt");
        var work = new Work("m1", correlationId: "order-1");
        _tracker.Start(work, counter, "SendReceiptHandler");
        _time.Advance(TimeSpan.FromMilliseconds(5));

        _tracker.Complete(work, WorkOutcome.Retried, "InvalidOperationException: Mailbox does not exist");

        var recent = Assert.Single(_tracker.GetSnapshot().Recent);
        Assert.Equal(WorkOutcome.Retried, recent.Outcome);
        Assert.Equal("InvalidOperationException: Mailbox does not exist", recent.Error);
        Assert.Equal("SendReceiptHandler", recent.Handler);
        Assert.Equal("order-1", recent.CorrelationId);
        Assert.Equal(TimeSpan.FromMilliseconds(5), recent.Duration);
        Assert.Equal(_time.GetUtcNow(), recent.CompletedAt);
        Assert.Equal(recent.CompletedAt - recent.Duration, recent.StartedAt);
    }

    [Fact]
    public void Complete_WhenSlowOrProgressReportingWorkSucceeds_KeepsItInRecent()
    {
        var counter = _tracker.GetCounter(WorkKind.Message, "orders.placed", "queue:orders");
        var slow = new Work("slow");
        var reporting = new Work("reporting");
        var fast = new Work("fast");
        _tracker.Start(slow, counter, null);
        _tracker.Start(reporting, counter, null);
        _tracker.Start(fast, counter, null);

        reporting.Report(100, "done");
        _tracker.Complete(reporting, WorkOutcome.Succeeded, null);
        _tracker.Complete(fast, WorkOutcome.Succeeded, null);
        _time.Advance(TimeSpan.FromSeconds(2));
        _tracker.Complete(slow, WorkOutcome.Succeeded, null);

        var recent = _tracker.GetSnapshot().Recent;
        Assert.Equal(["slow", "reporting"], recent.Select(r => r.Id));
        Assert.Equal(100, recent[1].Progress);
        Assert.Equal("done", recent[1].ProgressMessage);
        Assert.Equal(3, Assert.Single(_tracker.GetSnapshot().Stats).Succeeded);
    }

    [Fact]
    public void Recent_WhenFull_KeepsTheNewestItems()
    {
        var counter = _tracker.GetCounter(WorkKind.Job, "cleanup", null);

        for (int i = 1; i <= 5; i++)
            _tracker.Record(counter, WorkOutcome.Failed, $"job-{i}", 1, null, "failed");

        Assert.Equal(["job-5", "job-4", "job-3"], _tracker.GetSnapshot().Recent.Select(r => r.Id));
        Assert.Equal(5, Assert.Single(_tracker.GetSnapshot().Stats).Failed);
    }

    [Fact]
    public void Record_WhenWorkEndsWithoutRunning_CountsItAndKeepsOnlyFailures()
    {
        var counter = _tracker.GetCounter(WorkKind.Message, "orders.unknown", "subscription:orders/billing");

        _tracker.Record(counter, WorkOutcome.Skipped, "m1", 1, null, null);
        _tracker.Record(counter, WorkOutcome.DeadLettered, "m2", 3, "order-2", "No handler for message type \"orders.unknown\"");

        var snapshot = _tracker.GetSnapshot();
        var stats = Assert.Single(snapshot.Stats);
        Assert.Equal((1, 1, 0), (stats.Skipped, stats.DeadLettered, stats.Timed));
        var recent = Assert.Single(snapshot.Recent);
        Assert.Equal(("m2", 3, "order-2", WorkOutcome.DeadLettered), (recent.Id, recent.Attempt, recent.CorrelationId, recent.Outcome));
        Assert.Null(recent.Handler);
    }

    [Fact]
    public void GetSnapshot_AfterTheWindowPasses_DropsOldCounts()
    {
        var counter = _tracker.GetCounter(WorkKind.Job, "cleanup", null);
        _tracker.Record(counter, WorkOutcome.Succeeded, "old", 1, null, null);

        _time.Advance(TimeSpan.FromMinutes(5));
        _tracker.Record(counter, WorkOutcome.Succeeded, "recent", 1, null, null);
        Assert.Equal(2, Assert.Single(_tracker.GetSnapshot().Stats).Succeeded);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, Assert.Single(_tracker.GetSnapshot().Stats).Succeeded);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(_tracker.GetSnapshot().Stats);
    }

    [Fact]
    public void GetSnapshot_AfterSeveralRuns_SumsDurationsAndKeepsTheLongest()
    {
        var counter = _tracker.GetCounter(WorkKind.Message, "orders.placed", "queue:orders");
        foreach (int milliseconds in new[] { 10, 30, 20 })
        {
            var work = new Work($"m{milliseconds}");
            _tracker.Start(work, counter, null);
            _time.Advance(TimeSpan.FromMilliseconds(milliseconds));
            _tracker.Complete(work, milliseconds == 30 ? WorkOutcome.DeadLettered : WorkOutcome.Succeeded, milliseconds == 30 ? "poison" : null);
        }

        var stats = Assert.Single(_tracker.GetSnapshot().Stats);
        Assert.Equal((2, 1, 3), (stats.Succeeded, stats.DeadLettered, stats.Timed));
        Assert.Equal(TimeSpan.FromMilliseconds(60), stats.TotalDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(30), stats.MaxDuration);
    }

    [Fact]
    public void HasChangedSince_WhenWorkPassesTheThresholdOrFinishesVisibly_ReturnsTrue()
    {
        var counter = _tracker.GetCounter(WorkKind.Job, "cleanup", null);
        var work = new Work("job-1");
        _tracker.Start(work, counter, null);
        long version = _tracker.Version;
        long published = _time.GetTimestamp();

        _time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(_tracker.HasChangedSince(version, published));

        _time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True(_tracker.HasChangedSince(version, published));
        Assert.False(_tracker.HasChangedSince(version, _time.GetTimestamp()));

        _tracker.Complete(work, WorkOutcome.Succeeded, null);
        Assert.True(_tracker.HasChangedSince(version, _time.GetTimestamp()));
    }

    [Fact]
    public void GetCounter_BeyondTheLimit_CountsUnderOther()
    {
        for (int i = 0; i < WorkTracker.MaxCounters; i++)
            _tracker.GetCounter(WorkKind.Message, $"type-{i}", "subscription:t/s");

        var overflow = _tracker.GetCounter(WorkKind.Message, "one-too-many", "subscription:t/s");

        Assert.Equal(WorkTracker.OtherName, overflow.Name);
        Assert.Null(overflow.Source);
        Assert.Same(overflow, _tracker.GetCounter(WorkKind.Message, "and-another", "subscription:t/s"));
        Assert.Equal("type-1", _tracker.GetCounter(WorkKind.Message, "type-1", "subscription:t/s").Name);
    }

    [Fact]
    public void Truncate_WithALongError_CutsItToTheLimit()
    {
        string error = new string('e', WorkTracker.MaxErrorLength - 4) + "😀" + new string('x', 100);

        string truncated = WorkTracker.Truncate(error)!;

        Assert.True(truncated.Length <= WorkTracker.MaxErrorLength);
        Assert.EndsWith("...", truncated);
        Assert.False(Char.IsHighSurrogate(truncated[^4]));
        Assert.Equal("short", WorkTracker.Truncate("short"));
        Assert.Null(WorkTracker.Truncate(null));
    }

    private sealed class Work(string id, int attempt = 1, string? correlationId = null) : TrackedWork
    {
        internal override string TrackedId => id;
        internal override int TrackedAttempt => attempt;
        internal override string? TrackedCorrelationId => correlationId;

        public void Report(int? percent, string? message) => UpdateProgress(percent, message);
    }
}
