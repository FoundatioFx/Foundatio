using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Foundatio.Tests;

public class DeclarativeRegistrationTests
{
    [Fact]
    public async Task AddHandler_SendGoesToOneHandlerAndPublishReachesSubscriptionAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var probe = new HandlerProbe();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(probe);
        services.AddFoundatio()
            .Messaging.UseInMemory()
            .Messaging.AddHandler<HandledOrder, OrderHandler>()                                       // class handler
            .Messaging.AddHandler<HandledTask>((message, _) => { probe.Record($"task:{message.Message.Id}"); return Task.CompletedTask; }); // delegate handler

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Single(hosted); // one auto-registered hosted service drives every handler

        foreach (var service in hosted)
            await service.StartAsync(cancellationToken);

        try
        {
            var bus = provider.GetRequiredService<IMessageBus>();

            // The caller's verb decides delivery; the same registration serves both.
            await bus.SendAsync(new HandledOrder { Id = "sent" }, cancellationToken: cancellationToken);
            await bus.PublishAsync(new HandledOrder { Id = "published" }, cancellationToken: cancellationToken);
            await bus.SendAsync(new HandledTask { Id = "t1" }, cancellationToken: cancellationToken);

            Assert.True(await probe.WaitForAsync(3, TimeSpan.FromSeconds(10)), $"handled: {String.Join(",", probe.Events)}");
            Assert.Contains("order:sent", probe.Events);
            Assert.Contains("order:published", probe.Events);
            Assert.Contains("task:t1", probe.Events);

            // Same type sent AND published: exactly one delivery per verb — the queue and topic namespaces are
            // segregated, so a send is never fanned out and a publish is never consumed as queue work.
            Assert.Equal(3, probe.Events.Count);
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task AddHandler_PublishIsOncePerServiceUnlessPerInstanceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();

        // Two service providers sharing one transport simulate two scaled instances of the same service.
        var sharedProbe = new HandlerProbe();
        var instanceA = BuildInstance(transport, sharedProbe);
        var instanceB = BuildInstance(transport, sharedProbe);

        await using (instanceA.Provider)
        await using (instanceB.Provider)
        {
            await StartAsync(instanceA, cancellationToken);
            await StartAsync(instanceB, cancellationToken);

            try
            {
                var bus = instanceA.Provider.GetRequiredService<IMessageBus>();

                // Default subscription = service identity, shared by both instances => they compete: one copy total.
                await bus.PublishAsync(new HandledEvent { Id = "shared" }, cancellationToken: cancellationToken);
                Assert.True(await sharedProbe.WaitForAsync(1, TimeSpan.FromSeconds(10)), $"handled: {String.Join(",", sharedProbe.Events)}");
                await Task.Delay(250, cancellationToken);
                Assert.Single(sharedProbe.Events, e => e.StartsWith("event:", StringComparison.Ordinal));

                // PerInstance handlers each take a unique subscription => every instance receives its own copy.
                await bus.PublishAsync(new HandledBroadcast { Id = "all" }, cancellationToken: cancellationToken);
                Assert.True(await sharedProbe.WaitForAsync(3, TimeSpan.FromSeconds(10)), $"handled: {String.Join(",", sharedProbe.Events)}");
                Assert.Equal(2, sharedProbe.Events.Count(e => e.StartsWith("broadcast:", StringComparison.Ordinal)));
            }
            finally
            {
                await StopAsync(instanceA, cancellationToken);
                await StopAsync(instanceB, cancellationToken);
            }
        }
    }

    [Fact]
    public async Task AddCronJob_RegistersDefinitionAndSchedulesWhenPumpStartsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFoundatio()
            .Jobs.UseInMemoryRuntime()
            .Jobs.AddCronJob<CronProbeJob>("* * * * *", o => o.Scope = ScheduledJobScope.PerNode);

        await using var provider = services.BuildServiceProvider();

        // The builder records the schedule as a DI singleton with the requested scope and a type-derived name.
        var definition = Assert.Single(provider.GetServices<ScheduledJobDefinition>());
        Assert.Equal(typeof(CronProbeJob), definition.JobType);
        Assert.Equal(ScheduledJobScope.PerNode, definition.Scope);
        Assert.Equal(nameof(CronProbeJob), definition.Name);

        // Starting the runtime pump schedules registered CRON jobs into the scheduler — no manual ScheduleAsync call.
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(cancellationToken);

        try
        {
            var scheduler = provider.GetRequiredService<IJobScheduler>();
            ScheduledJobDefinition? scheduled = null;
            long deadline = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < deadline)
            {
                scheduled = (await scheduler.GetSchedulesAsync(cancellationToken)).FirstOrDefault(s => s.Name == nameof(CronProbeJob));
                if (scheduled is not null)
                    break;
                await Task.Delay(25, cancellationToken);
            }

            Assert.NotNull(scheduled);
            Assert.Equal(ScheduledJobScope.PerNode, scheduled!.Scope);
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(cancellationToken);
        }
    }

    private static (ServiceProvider Provider, List<IHostedService> Hosted) BuildInstance(InMemoryMessageTransport transport, HandlerProbe probe)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(probe);
        services.AddFoundatio()
            .Messaging.UseTransport(transport)
            .Messaging.AddHandler<HandledEvent, EventHandler>()
            .Messaging.AddHandler<HandledBroadcast, BroadcastHandler>(o => o.PerInstance = true);

        var provider = services.BuildServiceProvider();
        return (provider, provider.GetServices<IHostedService>().ToList());
    }

    private static async Task StartAsync((ServiceProvider Provider, List<IHostedService> Hosted) instance, CancellationToken cancellationToken)
    {
        foreach (var service in instance.Hosted)
            await service.StartAsync(cancellationToken);
    }

    private static async Task StopAsync((ServiceProvider Provider, List<IHostedService> Hosted) instance, CancellationToken cancellationToken)
    {
        foreach (var service in instance.Hosted)
            await service.StopAsync(cancellationToken);
    }

    private sealed class HandlerProbe
    {
        private readonly ConcurrentBag<string> _events = new();
        public IReadOnlyCollection<string> Events => _events;
        public void Record(string value) => _events.Add(value);

        public async Task<bool> WaitForAsync(int count, TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (_events.Count >= count)
                    return true;
                await Task.Delay(25);
            }
            return _events.Count >= count;
        }
    }

    [MessageRoute("declarative-orders")]
    public class HandledOrder { public string Id { get; set; } = ""; }

    [MessageRoute("declarative-tasks")]
    public class HandledTask { public string Id { get; set; } = ""; }

    [MessageRoute("declarative-events")]
    public class HandledEvent { public string Id { get; set; } = ""; }

    [MessageRoute("declarative-broadcasts")]
    public class HandledBroadcast { public string Id { get; set; } = ""; }

    private sealed class OrderHandler(HandlerProbe probe) : IMessageHandler<HandledOrder>
    {
        public Task HandleAsync(IReceivedMessage<HandledOrder> message, CancellationToken cancellationToken)
        {
            probe.Record($"order:{message.Message.Id}");
            return Task.CompletedTask;
        }
    }

    private sealed class EventHandler(HandlerProbe probe) : IMessageHandler<HandledEvent>
    {
        public Task HandleAsync(IReceivedMessage<HandledEvent> message, CancellationToken cancellationToken)
        {
            probe.Record($"event:{message.Message.Id}");
            return Task.CompletedTask;
        }
    }

    private sealed class BroadcastHandler(HandlerProbe probe) : IMessageHandler<HandledBroadcast>
    {
        public Task HandleAsync(IReceivedMessage<HandledBroadcast> message, CancellationToken cancellationToken)
        {
            probe.Record($"broadcast:{message.Message.Id}");
            return Task.CompletedTask;
        }
    }

    private sealed class CronProbeJob : IJob
    {
        public Task<JobResult> RunAsync(JobExecutionContext context) => Task.FromResult(JobResult.Success);
    }
}
