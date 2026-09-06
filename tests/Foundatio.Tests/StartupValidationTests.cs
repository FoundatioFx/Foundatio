using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Messaging;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Foundatio.Tests;

public class StartupValidationTests
{
    [Fact]
    public async Task ProducerTopologyNone_DuplicateWireNames_FailsBeforePublishingAsync()
    {
        var services = new ServiceCollection();
        services.AddFoundatio().ConfigureMessaging(m => m.UseInMemory().ConfigureTopology(TopologyMode.None)
            .AddMessageType<Ping>("event.v1").AddMessageType<OtherPing>("event.v1"));
        services.AddMessagingTopology();
        await using var provider = services.BuildServiceProvider();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartHostedAsync(provider, TestContext.Current.CancellationToken));
        Assert.Contains("event.v1", error.Message);
    }

    private sealed record OtherPing;

    [Fact]
    public async Task CronJobWithoutRuntimeStore_FailsStartupWithActionableMessageAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddFoundatio().Jobs.AddCronJob<NoopJob>("* * * * *");

        services.AddLogging();
        services.AddMessageConsumers();
        services.AddJobScheduler();
        await using var provider = services.BuildServiceProvider();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartHostedAsync(provider, cancellationToken));
        Assert.Contains("UseInMemory", ex.Message);
        Assert.Contains("never run", ex.Message);
    }

    [Fact]
    public async Task HandlerWithoutTransport_FailsStartupWithActionableMessageAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddFoundatio().Messaging.AddConsumer<Ping>((_, _) => Task.CompletedTask);

        services.AddLogging();
        services.AddMessageConsumers();
        services.AddJobScheduler();
        await using var provider = services.BuildServiceProvider();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartHostedAsync(provider, cancellationToken));
        Assert.Contains("no message transport", ex.Message);
        Assert.Contains("UseTransport", ex.Message);
    }

    [Fact]
    public void AddCronJob_WithInvalidCron_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        Assert.ThrowsAny<Exception>(() => services.AddFoundatio().Jobs.AddCronJob<NoopJob>("not-a-cron"));
    }

    [Fact]
    public void AddCronJob_WithDuplicateName_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        var builder = services.AddFoundatio();
        builder.Jobs.AddCronJob<NoopJob>("* * * * *");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Jobs.AddCronJob<NoopJob>("*/5 * * * *"));
        Assert.Contains(nameof(NoopJob), ex.Message);
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public async Task ValidConfiguration_StartsCleanlyAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddFoundatio()
            .Messaging.UseInMemory()
            .AddConsumer<Ping>((_, _) => Task.CompletedTask)
            .Builder.Jobs.UseInMemory()
            .AddCronJob<NoopJob>("0 3 * * *");

        services.AddLogging();
        services.AddMessageConsumers();
        services.AddJobScheduler();
        await using var provider = services.BuildServiceProvider();
        await StartHostedAsync(provider, cancellationToken);
        await StopHostedAsync(provider, cancellationToken);
    }

    private static async Task StartHostedAsync(ServiceProvider provider, CancellationToken cancellationToken)
    {
        // Validators and hosts run in registration order, like the generic host would run them.
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(cancellationToken);
    }

    private static async Task StopHostedAsync(ServiceProvider provider, CancellationToken cancellationToken)
    {
        foreach (var hosted in provider.GetServices<IHostedService>().Reverse())
            await hosted.StopAsync(cancellationToken);
    }

    private sealed class Ping
    {
        public string? Data { get; set; }
    }

    private sealed class NoopJob : IJob
    {
        public Task<JobResult> RunAsync(JobExecutionContext context) => Task.FromResult(JobResult.Success);
    }
}
