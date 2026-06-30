using System;
using Foundatio.Jobs;
using Xunit;

namespace Foundatio.Tests.Jobs;

// Inherits every [Fact] from JobRuntimeStoreConformanceTests, so a new conformance check automatically runs here with
// no per-test override to forget.
public class InMemoryJobRuntimeStoreTests : JobRuntimeStoreConformanceTests
{
    public InMemoryJobRuntimeStoreTests(ITestOutputHelper output) : base(output) { }

    protected override IJobRuntimeStore CreateStore(TimeProvider timeProvider) => new InMemoryJobRuntimeStore(timeProvider);
}
