using System;
using System.Collections.Generic;

namespace Foundatio.Messaging {
    public class SharedMessageBusOptions : SharedOptions {
        /// <summary>
        /// The topic name
        /// </summary>
        public string Topic { get; set; } = "messages";

        /// <summary>
        /// Controls which types messages are mapped to.
        /// </summary>
        public Dictionary<string, Type> MessageTypeMappings { get; set; } = new Dictionary<string, Type>();

        /// <summary>
        /// Controls the way the messages are sent to subscribers
        /// </summary>
        public bool SendMessagesSynchronously { get; set; } = true;
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