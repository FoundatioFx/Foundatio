using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>State shared by the client and the processors of one messaging configuration.</summary>
internal sealed class MessagingRuntime
{
    private readonly List<MessageProcessor> _processors = [];
    private readonly ILogger _logger;
    private volatile ProcessingState _hostState;

    public MessagingRuntime(IMessageTransport transport, MessagingOptions options, MessageTypeRegistry types, ISerializer serializer,
        IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory, TimeProvider timeProvider, string serviceName)
    {
        Transport = transport;
        Options = options;
        Types = types;
        Serializer = serializer;
        ScopeFactory = scopeFactory;
        LoggerFactory = loggerFactory;
        TimeProvider = timeProvider;
        ServiceName = MessageTypeRegistry.NormalizeName(serviceName);
        _logger = loggerFactory.CreateLogger<MessagingRuntime>();
    }

    public IMessageTransport Transport { get; }
    public MessagingOptions Options { get; }
    public MessageTypeRegistry Types { get; }
    public ISerializer Serializer { get; }
    public IServiceScopeFactory ScopeFactory { get; }
    public ILoggerFactory LoggerFactory { get; }
    public TimeProvider TimeProvider { get; }
    public string ServiceName { get; }

    /// <summary>Notified of sends and settled deliveries; set by the test harness.</summary>
    public IMessagingObserver? Observer { get; init; }

    /// <summary>Whether the hosted service is waiting for the startup gate, has started the processors, or has stopped.</summary>
    public ProcessingState HostState
    {
        get => _hostState;
        set => _hostState = value;
    }

    public MessageProcessor[] GetProcessors()
    {
        lock (_processors)
            return _processors.ToArray();
    }

    public MessageSource GetSource(MessageHandlerRegistration registration)
    {
        var route = Types.GetRoute(registration.MessageType);
        if (registration.Kind == MessageHandlerKind.Consumer)
            return MessageSource.Queue(route.Queue);

        return registration.Scope == SubscriptionScope.Instance
            ? MessageSource.InstanceSubscription(route.Topic, ServiceName)
            : MessageSource.Subscription(route.Topic, registration.SubscriptionName ?? ServiceName);
    }

    /// <summary>Creates a processor and opens its receiver. When <paramref name="retryInBackground"/> is true a failure is logged and retried.</summary>
    public async Task<MessageProcessor> StartProcessorAsync(MessageSource source, IReadOnlyList<MessageHandlerRegistration> handlers, bool retryInBackground, CancellationToken cancellationToken)
    {
        var processor = new MessageProcessor(this, source, handlers);
        lock (_processors)
            _processors.Add(processor);

        try
        {
            await processor.StartAsync(cancellationToken).AnyContext();
        }
        catch (Exception ex) when (retryInBackground && !cancellationToken.IsCancellationRequested)
        {
            processor.Status.RecordFailure(ex);
            _logger.LogError(ex, "Could not start receiving from {Source}; retrying in the background", source);
            _ = Task.Run(() => RetryStartAsync(processor), CancellationToken.None);
        }
        catch
        {
            lock (_processors)
                _processors.Remove(processor);
            throw;
        }

        return processor;
    }

    public async Task StopProcessorAsync(MessageProcessor processor, CancellationToken cancellationToken = default)
    {
        lock (_processors)
            _processors.Remove(processor);

        await processor.StopAsync(Options.ShutdownTimeout, cancellationToken).AnyContext();
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        MessageProcessor[] processors;
        lock (_processors)
        {
            processors = _processors.ToArray();
            _processors.Clear();
        }

        await Task.WhenAll(processors.Select(p => p.StopAsync(Options.ShutdownTimeout, cancellationToken))).AnyContext();
    }

    private async Task RetryStartAsync(MessageProcessor processor)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            lock (_processors)
            {
                if (!_processors.Contains(processor))
                    return;
            }

            await TimeProvider.SafeDelay(delay).AnyContext();
            try
            {
                await processor.StartAsync(CancellationToken.None).AnyContext();
                _logger.LogInformation("Started receiving from {Source}", processor.Source);
                return;
            }
            catch (Exception ex)
            {
                processor.Status.RecordFailure(ex);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
                _logger.LogError(ex, "Could not start receiving from {Source}; retrying in {Delay}", processor.Source, delay);
            }
        }
    }
}
