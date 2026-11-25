using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Testing;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Jobs;

/// <summary>Concurrency and deduplication keys under <see cref="FoundatioTestHarness"/>, the way app tests use them.</summary>
public class DurableJobsHarnessTests : TestWithLoggingBase
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public DurableJobsHarnessTests(ITestOutputHelper output) : base(output)
    {
        _output = output;
    }

    [Fact]
    public async Task ProcessPendingAsync_RunsJobsWaitingForAConcurrencyKeyInTurnAndOneJobPerDeduplicationKey()
    {
        var harness = await StartPausedAsync();
        var jobs = _host!.Services.GetRequiredService<IJobClient>();

        for (int i = 0; i < 3; i++)
            await jobs.EnqueueAsync(new ResetProject("p1", i), new EnqueueOptions { ConcurrencyKey = "p1" }, TestCancellationToken);
        var notify = await jobs.EnqueueAsync(new NotifyOrganization("o1"), new EnqueueOptions { DeduplicationKey = "o1" }, TestCancellationToken);
        var duplicate = await jobs.EnqueueAsync(new NotifyOrganization("o1"), new EnqueueOptions { DeduplicationKey = "o1" }, TestCancellationToken);
        Assert.Equal(notify.Id, duplicate.Id);

        var processed = await harness.ProcessPendingAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(4, processed.JobRuns.Count);
        Assert.All(processed.JobRuns, run => Assert.Equal((JobState.Succeeded, 1), (run.State, run.Attempt)));
        Assert.Equal([0, 1, 2], processed.JobRuns.Select(r => r.Arguments).OfType<ResetProject>().Select(r => r.Sequence));
        var afterRun = await jobs.EnqueueAsync(new NotifyOrganization("o1"), new EnqueueOptions { DeduplicationKey = "o1" }, TestCancellationToken);
        Assert.NotEqual(notify.Id, afterRun.Id);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }

        await base.DisposeAsync();
    }

    private async Task<FoundatioTestHarness> StartPausedAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddTestLogger(_output);
        builder.Services.AddFoundatioJobs(j => j.UseInMemory().AddJob<ResetProjectJob>().AddJob<NotifyOrganizationJob>());
        builder.Services.AddFoundatioTestHarness(o => o.StartPaused = true);
        _host = builder.Build();
        await _host.StartAsync(TestCancellationToken);
        return _host.Services.GetRequiredService<FoundatioTestHarness>();
    }

    public record ResetProject(string ProjectId, int Sequence);

    public record NotifyOrganization(string OrganizationId);

    public sealed class ResetProjectJob : IJobHandler<ResetProject>
    {
        private static readonly Dictionary<string, int> Running = new();

        public async Task HandleAsync(ResetProject arguments, IJobContext context, CancellationToken cancellationToken)
        {
            lock (Running)
            {
                if (Running.TryGetValue(arguments.ProjectId, out int count) && count > 0)
                    throw new InvalidOperationException($"Another reset of {arguments.ProjectId} is running.");
                Running[arguments.ProjectId] = 1;
            }

            try
            {
                await Task.Delay(20, cancellationToken);
            }
            finally
            {
                lock (Running)
                    Running[arguments.ProjectId] = 0;
            }
        }
    }

    public sealed class NotifyOrganizationJob : IJobHandler<NotifyOrganization>
    {
        public Task HandleAsync(NotifyOrganization arguments, IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
