using System;
using System.Threading;

namespace Foundatio.AWS.Tests.Messaging;

/// <summary>The system clock moved ahead by however much a test has advanced it, so hours can pass in a test.</summary>
public sealed class OffsetTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan amount) => Interlocked.Add(ref _offsetTicks, amount.Ticks);

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}
