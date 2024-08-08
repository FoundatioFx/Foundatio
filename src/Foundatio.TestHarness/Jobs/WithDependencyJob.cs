using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Jobs;

public class WithDependencyJob : JobBase
{
    public WithDependencyJob(MyDependency dependency, ILoggerFactory loggerFactory = null) : base(null, loggerFactory)
    {
        Dependency = dependency;
    }

    public MyDependency Dependency { get; private set; }

    public int RunCount { get; set; }

    protected override Task<JobResult> RunInternalAsync(JobContext context)
    {
        RunCount++;

        return Task.FromResult(JobResult.Success);
    }
}

public class MyDependency
{
    public int MyProperty { get; set; }
}
