namespace Foundatio.Messaging.Benchmarks;

public interface IMessagingDriver : IAsyncDisposable
{
    Task StartAsync(Action<int, LoadMessage> received, CancellationToken token);
    Task SendAsync(LoadMessage[] messages, CancellationToken token);
    void ThrowIfFaulted();
}
