using System;
using Foundatio.Lock;
using Foundatio.Xunit;
using Xunit;

namespace Foundatio.Tests.Locks;

public class LeaseTimestampTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Theory]
    [InlineData(2, 10, 10)]
    [InlineData(7, 2, 7)]
    [InlineData(3, -20, 3)]
    public void ElapsedUntil_WhenClocksDisagree_UsesLargerElapsedTime(int monotonicSeconds, int wallClockSeconds, int expectedSeconds)
    {
        // Arrange
        var clock = TimeProvider.System;
        var start = new LeaseTimestamp(clock.TimestampFrequency, DateTimeOffset.UnixEpoch);
        var end = new LeaseTimestamp((1L + monotonicSeconds) * clock.TimestampFrequency, DateTimeOffset.UnixEpoch.AddSeconds(wallClockSeconds));

        // Act
        var elapsed = start.ElapsedUntil(end, clock);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), elapsed);
    }
}
