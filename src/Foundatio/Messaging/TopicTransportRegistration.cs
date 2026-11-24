using System;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>
/// The transport <see cref="FoundatioMessagingBuilder.ForTopics"/> chose. One the service provider created is disposed
/// with it; one registered as an instance is not, since the provider never disposes instances.
/// </summary>
internal sealed class TopicTransportRegistration(IMessageTransport transport) : IAsyncDisposable, IDisposable
{
    public IMessageTransport Transport { get; } = transport;

    public ValueTask DisposeAsync() => Transport.DisposeAsync();

    public void Dispose()
    {
        if (Transport is IDisposable disposable)
            disposable.Dispose();
        else
            Transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
