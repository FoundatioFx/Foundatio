namespace Foundatio.Extensions.Hosting.Jobs;

public class ScheduledJobRegistration
{
    public ScheduledJobRegistration(ScheduledJobOptions options)
    {
        Options = options;
    }

    public ScheduledJobOptions Options { get; private set; }
}
