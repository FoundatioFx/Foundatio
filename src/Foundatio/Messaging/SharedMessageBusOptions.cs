using System;
using System.Collections.Generic;

namespace Foundatio.Messaging {
    public class SharedMessageBusOptions : SharedOptions {
        /// <summary>
        /// The topic name
        /// </summary>
        public string Topic { get; set; } = "messages";

        /// <summary>
        /// Controls how message types are serialized to/from strings.
        /// </summary>
        public ITypeNameSerializer TypeNameSerializer { get; set; }
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
        
        public TBuilder TypeNameSerializer(ITypeNameSerializer typeNameSerializer) {
            Target.TypeNameSerializer = typeNameSerializer;
            return (TBuilder)this;
        }
    }
}