using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Foundatio.Tests;

public class DeveloperExperienceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AddFoundatioWorker_StartsOnlyConfiguredFeaturesAsync(bool messaging, bool jobs)
    {
        var token = TestContext.Current.CancellationToken;
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddFoundatioWorker(foundatio =>
        {
            if (messaging)
                foundatio.Messaging.UseInMemory().Messaging.AddConsumer<Ping>((_, _) => { handled.TrySetResult(); return Task.CompletedTask; });
            if (jobs)
                foundatio.Jobs.UseInMemory().Jobs.AddCronJob<NoopJob>("0 2 * * *");
        });
        using var host = builder.Build();
        await host.StartAsync(token);
        try
        {
            var names = host.Services.GetServices<IHostedService>().Select(s => s.GetType().Name).ToArray();
            Assert.Equal(messaging, names.Contains("MessageHandlerHostedService"));
            Assert.Equal(jobs, names.Contains("JobWorkerService"));
            Assert.Equal(jobs, names.Contains("JobSchedulerService"));
            Assert.Equal(messaging && jobs, names.Contains("ScheduledMessageDispatcherService"));
            if (messaging)
            {
                await host.Services.GetRequiredService<IMessageBus>().SendAsync(new Ping(), cancellationToken: token);
                await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            if (jobs)
            {
                Assert.NotNull(await host.Services.GetRequiredService<IScheduledJobManager>().GetScheduleAsync(nameof(NoopJob), token));
                var handle = await host.Services.GetRequiredService<IJobClient>().EnqueueAsync<NoopJob>(cancellationToken: token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                while ((await handle.GetStateAsync(deadline.Token))!.Status != JobStatus.Completed)
                    await Task.Delay(10, deadline.Token);
            }
        }
        finally { await host.StopAsync(token); }
    }

    [Fact]
    public void AddFoundatioWorker_MissingDependencies_ExplainsTheFix()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddFoundatioWorker(f => f.Messaging.AddConsumer<Ping>((_, _) => Task.CompletedTask)));
        Assert.Contains("Messaging.Use", ex.Message);
        ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddFoundatioWorker(f => f.Jobs.AddJobType<NoopJob>()));
        Assert.Contains("Jobs.Use", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void AddSubscriber_InvalidDurableName_FailsAtRegistration(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddFoundatio().Messaging.AddSubscriber<Ping>((_, _) => Task.CompletedTask, name!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AddConsumer_InvalidOptions_FailsAtRegistration(int scenario)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddFoundatio().Messaging.AddConsumer<Ping>((_, _) => Task.CompletedTask, options =>
        {
            if (scenario == 0) options.MaxConcurrency = 0;
            if (scenario == 1) options.MaxAttempts = 0;
            if (scenario == 2) options.AckMode = (AckMode)99;
            if (scenario == 3) options.Destination = " ";
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AddCronJob_InvalidOptions_FailsAtRegistration(int scenario)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddFoundatio().Jobs.AddCronJob<NoopJob>("* * * * *", options =>
        {
            if (scenario == 0) options.MaxAttempts = 0;
            if (scenario == 1) options.MisfireWindow = TimeSpan.FromDays(2);
            if (scenario == 2) options.Scope = (ScheduledJobScope)99;
            if (scenario == 3) options.Name = " ";
        }));
    }

    [Fact]
    public void AddJobType_AbstractJob_FailsAtRegistration()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddFoundatio().Jobs.AddJobType<IJob>());
    }

    [Fact]
    public async Task AddTemporarySubscriber_StatesItsLifetimeExplicitlyAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddFoundatioWorker(f => f.Messaging.UseInMemory()
            .Messaging.AddTemporarySubscriber<Ping>((_, _) => { received.TrySetResult(); return Task.CompletedTask; }));
        using var host = builder.Build();
        await host.StartAsync(token);
        try
        {
            await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new Ping(), cancellationToken: token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        finally { await host.StopAsync(token); }
    }

    private sealed record Ping;
    private sealed class NoopJob : IJob
    {
        public Task<JobResult> RunAsync(JobExecutionContext context) => Task.FromResult(JobResult.Success);
    }
}
