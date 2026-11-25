using System.Diagnostics.Metrics;

namespace Foundatio.Jobs;

/// <summary>Foundatio's durable job metrics, and the attribute names its job metrics and spans use.</summary>
internal static class JobsInstruments
{
    public const string JobId = "foundatio.job.id";
    public const string JobName = "foundatio.job.name";

    public static readonly Counter<long> Enqueued = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.jobs.enqueued", "{job}", "Jobs enqueued");
    public static readonly Counter<long> Completed = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.jobs.completed", "{run}", "Job runs finished, by outcome");
    public static readonly Histogram<double> Duration = FoundatioDiagnostics.Meter.CreateHistogram<double>("foundatio.jobs.duration", "s", "Time spent running a job");
}
