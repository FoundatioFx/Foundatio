using System;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class ConfigurationExperienceTests
{
    [Fact]
    public async Task MessagingOnly_InMemory_PersistsDelayedMessages()
    {
        var services = new ServiceCollection();
        services.AddFoundatio().ConfigureMessaging(m => m.UseInMemory().AddMessageType<Event>("event.v1", topic: "events"));
        await using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IJobClient>());
        Assert.NotNull(provider.GetService<IScheduledDispatchStore>());
        await provider.GetRequiredService<IMessageBus>().PublishAsync(new Event(), new MessagePublishOptions { Delay = TimeSpan.FromHours(1) }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConsumerRegistration_BindsWireNameAndProducerRoute()
    {
        var services = new ServiceCollection();
        services.AddFoundatio().ConfigureMessaging(m => m.UseInMemory().AddConsumer<Event>((_, _) => Task.CompletedTask,
            o => { o.MessageTypeName = "event.v1"; o.Destination = "work"; }));
        await using var provider = services.BuildServiceProvider();
        var router = provider.GetRequiredService<IMessageRouter>();
        Assert.Equal("work", router.ResolveRoute(new MessageRouteContext { MessageType = typeof(Event), Role = MessageRouteRole.QueueDestination }));
        Assert.Equal("event.v1", provider.GetRequiredService<IMessageTypeRegistry>().GetName(typeof(Event)));
    }

    public sealed record Event;
}
