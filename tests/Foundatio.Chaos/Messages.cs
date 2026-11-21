using Foundatio.Jobs;
using Foundatio.Messaging;

namespace Foundatio.Chaos;

/// <summary>How a handler treats a message: succeed, fail its first attempt, or fail every attempt.</summary>
public enum FailMode
{
    None = 0,
    FirstAttempt = 1,
    Always = 2
}

[Message("chaos.work")]
public sealed record WorkMessage(string Id, int Seq, FailMode Fail, int SlowMs);

[Message("chaos.grouped")]
public sealed record GroupedMessage(string Id, string Group, int Seq, long DueMs, FailMode Fail);

[Message("chaos.delayed")]
public sealed record DelayedMessage(string Id, int Seq, long DueMs);

[Message("chaos.event")]
public sealed record ChaosEvent(string Id, int Seq, FailMode Fail);

[Job(Name = "chaos.job")]
public sealed record ChaosJob(string Id, int Seq, long DueMs, FailMode Fail, int SlowMs);

public static class Streams
{
    public const string Work = "work";
    public const string Grouped = "grouped";
    public const string Delayed = "delayed";
    public const string Event = "event";
    public const string Job = "job";

    public const string WorkQueue = "chaos.work";
    public const string GroupedQueue = "chaos.grouped";
    public const string DelayedQueue = "chaos.delayed";
    public const string EventTopic = "chaos.event";
    public const string ServiceName = "chaos";
}
