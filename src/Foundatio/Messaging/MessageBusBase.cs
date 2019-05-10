using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging {
    public abstract class MessageBusBase<TOptions> : MaintenanceBase, IMessageBus, IDisposable where TOptions : SharedMessageBusOptions {
        protected readonly List<IMessageSubscription> _subscriptions = new List<IMessageSubscription>();
        protected readonly TOptions _options;
        protected readonly ISerializer _serializer;
        protected readonly ITypeNameSerializer _typeNameSerializer;
        protected readonly IMessageStore _store;
        private bool _isDisposed;

        public MessageBusBase(TOptions options) : base(options.LoggerFactory) {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            var loggerFactory = options?.LoggerFactory ?? NullLoggerFactory.Instance;
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            _typeNameSerializer = options.TypeNameSerializer ?? new DefaultTypeNameSerializer(_logger);
            _store = options.MessageStore ?? new InMemoryMessageStore(_logger);
            MessageBusId = Guid.NewGuid().ToString("N");
            InitializeMaintenance(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public string MessageBusId { get; protected set; }

        protected virtual Task ConfigureMessageType(Type messageType, CancellationToken cancellationToken) => Task.CompletedTask;

        protected abstract Task PublishImplAsync(byte[] body, MessagePublishOptions options = null);

        public async Task PublishAsync(object message, MessagePublishOptions options) {
            if (message == null)
                return;
            
            if (options.MessageType == null)
                options.MessageType = message.GetType();
            
            if (options.CancellationToken.IsCancellationRequested)
                return;

            if (options.ExpiresAtUtc.HasValue && options.ExpiresAtUtc.Value < SystemClock.UtcNow)
                return;
            
            await ConfigureMessageType(options.MessageType, options.CancellationToken).AnyContext();
            var body = _serializer.SerializeToBytes(message);

            if (options.DeliverAtUtc.HasValue && options.DeliverAtUtc > SystemClock.UtcNow) {
                var typeName = _typeNameSerializer.Serialize(options.MessageType);
                await _store.AddAsync(new PersistedMessage {
                    Id = Guid.NewGuid().ToString("N"),
                    PublishedUtc = SystemClock.UtcNow,
                    CorrelationId = options.CorrelationId,
                    MessageTypeName = typeName,
                    Body = body,
                    ExpiresAtUtc = options.ExpiresAtUtc,
                    DeliverAtUtc = options.DeliverAtUtc,
                    Properties = options.Properties
                });

                ScheduleNextMaintenance(options.DeliverAtUtc.Value);

                return;
            }

            await PublishImplAsync(body, options).AnyContext();
        }

        protected abstract Task<IMessageSubscription> SubscribeImplAsync(MessageSubscriptionOptions options, Func<IMessageContext, Task> handler);

        public async Task<IMessageSubscription> SubscribeAsync(MessageSubscriptionOptions options, Func<IMessageContext, Task> handler) {
            if (options.MessageType == null)
                throw new ArgumentNullException("Options must have a MessageType specified.");
            
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Adding subscription for {MessageType}.", options.MessageType.FullName);
            
            if (options.CancellationToken.IsCancellationRequested)
                return null;

            await ConfigureMessageType(options.MessageType, options.CancellationToken).AnyContext();
            var subscription = await SubscribeImplAsync(options, handler).AnyContext();
            _subscriptions.Add(subscription);

            return subscription;
        }

        protected bool MessageTypeHasSubscribers(Type messageType) {
            var subscribers = _subscriptions.Where(s => s.MessageType.IsAssignableFrom(messageType)).ToList();
            return subscribers.Count == 0;
        }

        protected override async Task<DateTime?> DoMaintenanceAsync() {
            var pendingMessages = await _store.GetReadyForDeliveryAsync();
            foreach (var pendingMessage in pendingMessages) {
                var messageType = _typeNameSerializer.Deserialize(pendingMessage.MessageTypeName);
                var properties = new Dictionary<string, string>();
                properties.AddRange(pendingMessage.Properties);
                await PublishImplAsync(pendingMessage.Body, new MessagePublishOptions {
                    CorrelationId = pendingMessage.CorrelationId,
                    DeliverAtUtc = pendingMessage.DeliverAtUtc,
                    ExpiresAtUtc = pendingMessage.ExpiresAtUtc,
                    MessageType = messageType,
                    Properties = properties
                }).AnyContext();
            }

            _subscriptions.RemoveAll(s => s.IsCancelled);

            return null;
        }

        public override void Dispose() {
            if (_isDisposed) {
                _logger.LogTrace("MessageBus {0} dispose was already called.", MessageBusId);
                return;
            }
            
            _isDisposed = true;
            
            _logger.LogTrace("MessageBus {0} dispose", MessageBusId);

            if (_subscriptions != null && _subscriptions.Count > 0) {
                foreach (var subscription in _subscriptions)
                    subscription.Dispose();
            }
        }
    }
}