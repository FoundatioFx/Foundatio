using Foundatio.Jobs.Store;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class InMemoryJobStoreTests(ITestOutputHelper output) : JobStoreTestsBase(output)
{
    protected override IJobStore CreateStore() => new InMemoryJobStore();
}
