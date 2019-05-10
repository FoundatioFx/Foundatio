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
        private readonly List<InMemoryStoredMessage> _messages = new List<InMemoryStoredMessage>();
        private readonly ILogger _logger;

        public InMemoryMessageStore(ILogger logger) {
            _logger = logger;
        }

        public Task AddAsync(PersistedMessage message) {
            _messages.Add(new InMemoryStoredMessage(message));
            return Task.CompletedTask;
        }

        public Task<ICollection<PersistedMessage>> GetReadyForDeliveryAsync() {
            var dueList = new List<PersistedMessage>();
            foreach (var message in _messages) {
                if (!message.IsProcessing && message.Message.DeliverAtUtc < SystemClock.UtcNow && message.MarkProcessing())
                    dueList.Add(message.Message);
            }

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

        protected class InMemoryStoredMessage {
            public InMemoryStoredMessage(PersistedMessage message) {
                Message = message;
            }
            
            public PersistedMessage Message { get; set; }
            public bool IsProcessing {
                get {
                    if (_processing != 0 && _startedProcessing < SystemClock.Now.Subtract(TimeSpan.FromMinutes(1)))
                        _processing = 0;
                    
                    return _processing != 0;
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