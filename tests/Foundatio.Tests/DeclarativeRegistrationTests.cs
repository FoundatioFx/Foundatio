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
    public async Task AddHandlers_HostAndDispatchQueueAndBroadcastMessagesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var probe = new HandlerProbe();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(probe);
        services.AddFoundatio()
            .Messaging.UseInMemory()
            .Messaging.AddQueueHandler<HandledOrder, OrderHandler>()                                    // class handler, competing
            .Messaging.AddQueueHandler<HandledTask>((message, _) => { probe.Record($"task:{message.Message.Id}"); return Task.CompletedTask; }) // delegate handler
            .Messaging.AddBroadcastHandler<HandledEvent, EventHandler>();                                // class handler, fan-out

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Single(hosted); // exactly one auto-registered hosted service drives every handler

        foreach (var service in hosted)
            await service.StartAsync(cancellationToken);

        try
        {
            await provider.GetRequiredService<Foundatio.Messaging.IQueue>().EnqueueAsync(new HandledOrder { Id = "o1" }, cancellationToken: cancellationToken);
            await provider.GetRequiredService<Foundatio.Messaging.IQueue>().EnqueueAsync(new HandledTask { Id = "t1" }, cancellationToken: cancellationToken);
            await provider.GetRequiredService<IPubSub>().PublishAsync(new HandledEvent { Id = "e1" }, cancellationToken: cancellationToken);

            Assert.True(await probe.WaitForAsync(3, TimeSpan.FromSeconds(10)), $"handled: {string.Join(",", probe.Events)}");
            Assert.Contains("order:o1", probe.Events);
            Assert.Contains("task:t1", probe.Events);
            Assert.Contains("event:e1", probe.Events);
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(cancellationToken);
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

    private sealed class CronProbeJob : IJob
    {
        public Task<JobResult> RunAsync(JobExecutionContext context) => Task.FromResult(JobResult.Success);
    }
}
