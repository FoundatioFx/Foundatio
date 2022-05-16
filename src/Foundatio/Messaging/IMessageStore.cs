using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging {
    public interface IMessageStore {
        Task AddAsync(PersistedMessage message);
        Task RemoveAsync(string[] ids);
        Task<ICollection<PersistedMessage>> GetReadyForDeliveryAsync();
        Task RemoveAllAsync();
    }

    public class PersistedMessage {
        public string Id { get; set; }
        public DateTime PublishedUtc { get; set; }
        public string CorrelationId { get; set; }
        public string MessageTypeName { get; set; }
        public byte[] Body { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public DateTime? DeliverAtUtc { get; set; }
        public IReadOnlyDictionary<string, string> Properties { get; set; }
    }

    public class InMemoryMessageStore : IMessageStore {
        private readonly List<InMemoryPersistedMessage> _messages = new List<InMemoryPersistedMessage>();
        private readonly ILogger _logger;

        public InMemoryMessageStore(ILogger logger) {
            _logger = logger;
        }

        public Task AddAsync(PersistedMessage message) {
            _messages.Add(new InMemoryPersistedMessage(message));
            return Task.CompletedTask;
        }

        public Task<ICollection<PersistedMessage>> GetReadyForDeliveryAsync() {
            var dueList = new List<PersistedMessage>();
            foreach (var message in _messages) {
                if (message.IsProcessing)
                    continue;

                if (message.Message.DeliverAtUtc > SystemClock.UtcNow)
                    continue;

                if (!message.MarkProcessing())
                    continue;

                dueList.Add(message.Message);

                if (dueList.Count >= 100)
                    break;
            }

            if (_messages.Count <= 0)
                _logger.LogTrace("No messages ready for delivery.");
            else
                _logger.LogTrace("Got {Count} / {Total} messages ready for delivery.", dueList.Count, _messages.Count);

            return Task.FromResult<ICollection<PersistedMessage>>(dueList);
        }

        public Task RemoveAllAsync() {
            _messages.Clear();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string[] ids) {
            _messages.RemoveAll(m => ids.Contains(m.Message.Id));
            return Task.CompletedTask;
        }

        protected class InMemoryPersistedMessage {
            public InMemoryPersistedMessage(PersistedMessage message) {
                Message = message;
            }
            
            public PersistedMessage Message { get; set; }
            public bool IsProcessing {
                get {
                    if (_processing == 0)
                        return false;

                    // check for timeout
                    if (SystemClock.UtcNow.Subtract(_startedProcessing) > TimeSpan.FromMinutes(1)) {
                        _processing = 0;
                        return false;
                    }

                    return true;
                }
            }

            private int _processing = 0;
            private DateTime _startedProcessing = DateTime.MinValue;

            public bool MarkProcessing() {
                var result = Interlocked.Exchange(ref _processing, 1);
                _startedProcessing = SystemClock.Now;

                return result == 0;
            }
        }
    }
}