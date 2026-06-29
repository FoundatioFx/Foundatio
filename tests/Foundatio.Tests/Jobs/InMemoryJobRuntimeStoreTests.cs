using System;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class InMemoryJobRuntimeStoreTests : JobRuntimeStoreConformanceTests
{
    public InMemoryJobRuntimeStoreTests(ITestOutputHelper output) : base(output) { }

    protected override IJobRuntimeStore CreateStore(TimeProvider timeProvider) => new InMemoryJobRuntimeStore(timeProvider);

    [Fact]
    public override Task JobLifecycle_RoundTripsAndTransitionsAsync() => base.JobLifecycle_RoundTripsAndTransitionsAsync();

    [Fact]
    public override Task Query_FiltersByNameStatusAndLimitAsync() => base.Query_FiltersByNameStatusAndLimitAsync();

    [Fact]
    public override Task Leasing_ClaimRenewReleaseAndStealAsync() => base.Leasing_ClaimRenewReleaseAndStealAsync();

    [Fact]
    public override Task StaleRecovery_ReclaimsExpiredButNotLiveOrCronAsync() => base.StaleRecovery_ReclaimsExpiredButNotLiveOrCronAsync();

    [Fact]
    public override Task ScheduledDispatches_ClaimCompleteAndRescheduleAsync() => base.ScheduledDispatches_ClaimCompleteAndRescheduleAsync();

    [Fact]
    public override Task Concurrency_OptimisticControlElectsSingleWinnerAsync() => base.Concurrency_OptimisticControlElectsSingleWinnerAsync();
}
