namespace Foundatio.Messaging {
    public class InMemoryMessageBusOptions : SharedMessageBusOptions { }

    public class InMemoryMessageBusOptionsBuilder : OptionsBuilder<InMemoryMessageBusOptions>, ISharedMessageBusOptionsBuilder {}
}