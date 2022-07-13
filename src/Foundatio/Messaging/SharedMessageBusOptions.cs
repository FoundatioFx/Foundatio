using System;
using System.Collections.Generic;

namespace Foundatio.Messaging {
    public class SharedMessageBusOptions : SharedOptions {
        /// <summary>
        /// The topic name
        /// </summary>
        public string Topic { get; set; } = "messages";

        /// <summary>
        /// Resolves a message to a .NET type.
        /// </summary>
        public Func<IConsumeMessageContext, Type> MessageTypeResolver { get; set; }

        /// <summary>
        /// Statically configured message type mappings. <see cref="MessageTypeResolver"/> will be run first and then this dictionary will be checked.
        /// </summary>
        public Dictionary<string, Type> MessageTypeMappings { get; set; } = new Dictionary<string, Type>();
    }

    public interface IConsumeMessageContext {
        string MessageType { get; set; }
        IDictionary<string, string> Properties { get; }
    }

    public class ConsumeMessageContext : IConsumeMessageContext {
        public string MessageType { get; set; } 
        public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    }

    public class SharedMessageBusOptionsBuilder<TOptions, TBuilder> : SharedOptionsBuilder<TOptions, TBuilder>
        where TOptions : SharedMessageBusOptions, new()
        where TBuilder : SharedMessageBusOptionsBuilder<TOptions, TBuilder> {
        public TBuilder Topic(string topic) {
            if (string.IsNullOrEmpty(topic))
                throw new ArgumentNullException(nameof(topic));
            Target.Topic = topic;
            return (TBuilder)this;
        }

        public TBuilder MessageTypeResolver(Func<IConsumeMessageContext, Type> resolver) {
            if (resolver == null)
                throw new ArgumentNullException(nameof(resolver));
            Target.MessageTypeResolver = resolver;
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