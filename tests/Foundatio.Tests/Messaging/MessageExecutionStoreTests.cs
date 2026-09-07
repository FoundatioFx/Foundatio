using Foundatio.Messaging;

namespace Foundatio.Tests.Messaging;

public sealed class MessageExecutionStoreTests : MessageExecutionStoreConformanceTests
{
    protected override IMessageExecutionStore CreateStore() => new InMemoryMessageExecutionStore();
}
