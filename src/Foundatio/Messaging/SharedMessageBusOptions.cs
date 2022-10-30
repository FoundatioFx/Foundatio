using System;
using System.Collections.Generic;

namespace Foundatio.Messaging {
    public class SharedMessageBusOptions : SharedOptions {
        /// <summary>
        /// The default topic name
        /// </summary>
        public string DefaultTopic { get; set; } = "messages";

        /// <summary>
        /// Resolves message types
        /// </summary>
        public IMessageRouter Router { get; set; }

        /// <summary>
        /// Statically configured message type mappings. <see cref="Router"/> will be run first and then this dictionary will be checked.
        /// </summary>
        public Dictionary<string, Type> MessageTypeMappings { get; set; } = new Dictionary<string, Type>();
    }

    public interface IMessageRouter {
        // get topic from bus options, message and message options
        // get message type from message and options
        // get .net type from topic, message type and properties (headers)
        IConsumeMessageContext ToMessageType(Type messageType);
        Type ToClrType(IConsumeMessageContext context);
    }

    public interface IConsumeMessageContext {
        string Topic { get; set; }
        string MessageType { get; set; }
        IDictionary<string, string> Properties { get; }
    }

    public class ConsumeMessageContext : IConsumeMessageContext {
        public string Topic { get; set; }
        public string MessageType { get; set; } 
        public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    }

    public class SharedMessageBusOptionsBuilder<TOptions, TBuilder> : SharedOptionsBuilder<TOptions, TBuilder>
        where TOptions : SharedMessageBusOptions, new()
        where TBuilder : SharedMessageBusOptionsBuilder<TOptions, TBuilder> {
        public TBuilder Topic(string topic) {
            if (string.IsNullOrEmpty(topic))
                throw new ArgumentNullException(nameof(topic));
            Target.DefaultTopic = topic;
            return (TBuilder)this;
        }

        public TBuilder MessageTypeResolver(IMessageRouter resolver) {
            if (resolver == null)
                throw new ArgumentNullException(nameof(resolver));
            Target.Router = resolver;
            return (TBuilder)this;
        }

        public TBuilder MapMessageType<T>(string name) {
            if (Target.MessageTypeMappings == null)
                Target.MessageTypeMappings = new Dictionary<string, Type>();
            
            Target.MessageTypeMappings[name] = typeof(T);
            return (TBuilder)this;
        }

        public TBuilder MapMessageTypeToClassName<T>() {
            if (Target.MessageTypeMappings == null)
                Target.MessageTypeMappings = new Dictionary<string, Type>();
            
            Target.MessageTypeMappings[typeof(T).Name] = typeof(T);
            return (TBuilder)this;
        }
    }
}