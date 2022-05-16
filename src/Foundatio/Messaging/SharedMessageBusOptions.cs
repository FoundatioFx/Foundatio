namespace Foundatio.Messaging {
    public class SharedMessageBusOptions : SharedOptions {
        /// <summary>
        /// Controls how message types are serialized to/from strings.
        /// </summary>
        public ITypeNameSerializer TypeNameSerializer { get; set; }

        /// <summary>
        /// Used to store delayed messages.
        /// </summary>
        public IMessageStore MessageStore { get; set; }
    }

    public class SharedMessageBusOptionsBuilder<TOptions, TBuilder> : SharedOptionsBuilder<TOptions, TBuilder>
        where TOptions : SharedMessageBusOptions, new()
        where TBuilder : SharedMessageBusOptionsBuilder<TOptions, TBuilder> {
        
        public TBuilder TypeNameSerializer(ITypeNameSerializer typeNameSerializer) {
            Target.TypeNameSerializer = typeNameSerializer;
            return (TBuilder)this;
        }
        
        public TBuilder MessageStore(IMessageStore messageStore) {
            Target.MessageStore = messageStore;
            return (TBuilder)this;
        }
    }
}