using System;
using System.Collections.Generic;
using Foundatio.Serializer;
using RabbitMQ.Client.Events;

namespace Foundatio.Messaging {
    public class ExchangeOptions {
        public string Name { get; set; }
        public string Type { get; set; } = RabbitMQ.Client.ExchangeType.Fanout;
        public IDictionary<string, object> Arguments { get; set; }
        public bool SupportsDelay { get; set; }
        public bool IsDurable { get; set; } = true;
        public bool AutoDelete { get; set; }
        
        public static ExchangeOptions Default(string topic = "messages", bool supportsDelay = false) => new ExchangeOptions {
            Name = topic,
            SupportsDelay = supportsDelay
        };
    }
    
    public class QueueOptions {
        public string Name { get; set; }
        public IDictionary<string, object> Arguments { get; set; }
        public bool IsDurable { get; set; } = true;
        public bool IsExclusive { get; set; } = true;
        public bool AutoDelete { get; set; } = true;
        public string BindToRoutingKey { get; set; }
        public string BindToExchangeName { get; set; }
        
        public static QueueOptions Default(string topic = "messages") => new QueueOptions {
            BindToExchangeName = topic
        };
    }
    
    public class TopicOptions {
        public ExchangeOptions ExchangeOptions { get; set; } = new ExchangeOptions();
        public QueueOptions QueueOptions { get; set; } = new QueueOptions();
        public TimeSpan? MessageTimeToLive { get; set; }
        public string RoutingKey { get; set; }
        public AcknowledgementStrategy AcknowledgementStrategy { get; set; } = AcknowledgementStrategy.FireAndForget;
        public IMessageSerializer Serializer { get; set; }
        
        public static TopicOptions Default(string topic = "messages", bool supportsDelay = false, IMessageSerializer serializer = null) => new TopicOptions {
            ExchangeOptions = ExchangeOptions.Default(topic, supportsDelay),
            QueueOptions = QueueOptions.Default(topic),
            Serializer = serializer
        };
        
        public static TopicOptions FireAndForget(string topic = "messages", bool supportsDelay = false, IMessageSerializer serializer = null) => new TopicOptions {
            ExchangeOptions = new ExchangeOptions { Name = topic, IsDurable = false, SupportsDelay = supportsDelay },
            QueueOptions = new QueueOptions { BindToExchangeName = topic, IsDurable = false },
            Serializer = serializer
        };
        
        public static TopicOptions WorkerQueue(string name, string topic = "messages-queue", bool supportsDelay = false, IMessageSerializer serializer = null) => new TopicOptions {
            ExchangeOptions = ExchangeOptions.Default(topic, supportsDelay),
            QueueOptions = new QueueOptions { Name = name, BindToExchangeName = topic, IsDurable = true, AutoDelete = false, IsExclusive = false },
            Serializer = serializer,
            AcknowledgementStrategy = AcknowledgementStrategy.Automatic
        };
    }

    public enum AcknowledgementStrategy {
        FireAndForget,
        Automatic
    }

    public interface IMessageSerializer {
        byte[] SerializeMessage(string messageType, object message);
        MessageBusData ConvertToMessageBusData(BasicDeliverEventArgs envelope);
    }

    public class DefaultMessageSerializer : IMessageSerializer {
        private readonly ISerializer _serializer;
        
        public DefaultMessageSerializer(ISerializer serializer) {
            _serializer = serializer;
        }
        
        public byte[] SerializeMessage(string messageType, object message) {
            return _serializer.SerializeToBytes(message);
        }
        
        public MessageBusData ConvertToMessageBusData(BasicDeliverEventArgs envelope) {
            if (String.IsNullOrEmpty(envelope.BasicProperties.Type))
                return _serializer.Deserialize<MessageBusData>(envelope.Body);
            
            return new MessageBusData {
                Type = envelope.BasicProperties.Type,
                Data = envelope.Body
            };
        }
    }
}