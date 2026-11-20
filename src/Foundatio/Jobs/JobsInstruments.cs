using System.Diagnostics.Metrics;

namespace Foundatio.Jobs;

internal static class JobsInstruments
{
    public static readonly Counter<long> Enqueued = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.jobs.enqueued", description: "Jobs enqueued");
    public static readonly Counter<long> Completed = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.jobs.completed", description: "Job runs finished, by outcome");
    public static readonly Histogram<double> Duration = FoundatioDiagnostics.Meter.CreateHistogram<double>("foundatio.jobs.duration", "ms", "Time spent running a job");
}
