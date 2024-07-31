using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Tests.Extensions;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility;

public class ScheduledTimerTests : TestWithLoggingBase
{
    public ScheduledTimerTests(ITestOutputHelper output) : base(output)
    {
        Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);
    }

    [Fact]
    public Task CanRun()
    {
        var resetEvent = new AsyncAutoResetEvent();
        Task<DateTime?> Callback()
        {
            resetEvent.Set();
            return null;
        }

        using var timer = new ScheduledTimer(Callback, loggerFactory: Log);
        timer.ScheduleNext();
        return resetEvent.WaitAsync(new CancellationTokenSource(500).Token);
    }

    [Fact]
    public Task CanRunAndScheduleConcurrently()
    {
        return CanRunConcurrentlyAsync();
    }

    [Fact]
    public Task CanRunWithMinimumInterval()
    {
        return CanRunConcurrentlyAsync(TimeSpan.FromMilliseconds(100));
    }

    private async Task CanRunConcurrentlyAsync(TimeSpan? minimumIntervalTime = null)
    {
        const int iterations = 2;
        var countdown = new AsyncCountdownEvent(iterations);

        async Task<DateTime?> Callback()
        {
            _logger.LogInformation("Starting work");
            await Task.Delay(250);
            countdown.Signal();
            _logger.LogInformation("Finished work");
            return null;
        }

        using var timer = new ScheduledTimer(Callback, minimumIntervalTime: minimumIntervalTime, loggerFactory: Log);
        timer.ScheduleNext();
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                await Task.Delay(10);
                timer.ScheduleNext();
            }
        });

        _logger.LogInformation("Waiting for 300ms");
        await countdown.WaitAsync(TimeSpan.FromMilliseconds(300));
        _logger.LogInformation("Finished waiting for 300ms");
        Assert.Equal(iterations - 1, countdown.CurrentCount);

        _logger.LogInformation("Waiting for 1.5 seconds");
        await countdown.WaitAsync(TimeSpan.FromSeconds(1.5));
        _logger.LogInformation("Finished waiting for 1.5 seconds");
        Assert.Equal(0, countdown.CurrentCount);
    }

    [Fact]
    public async Task CanRecoverFromError()
    {
        int hits = 0;
        var resetEvent = new AsyncAutoResetEvent(false);

        Task<DateTime?> Callback()
        {
            Interlocked.Increment(ref hits);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Callback called for the #{Hits} time", hits);
            if (hits == 1)
                throw new Exception("Error in callback");

            resetEvent.Set();
            return Task.FromResult<DateTime?>(null);
        }

        using var timer = new ScheduledTimer(Callback, loggerFactory: Log);
        timer.ScheduleNext();
        await resetEvent.WaitAsync(new CancellationTokenSource(800).Token);
        Assert.Equal(2, hits);
    }
}
