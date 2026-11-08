using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;

namespace Foundatio.Testing;

/// <summary>An <see cref="IJobContext"/> for unit testing a job. Records reported progress.</summary>
/// <example>
/// <code>
/// var context = new TestJobContext();
/// await new GenerateReportJob(...).RunAsync(new GenerateReport("pdf"), context, CancellationToken.None);
/// Assert.Equal(100, context.Progress);
/// </code>
/// </example>
public sealed class TestJobContext : IJobContext
{
    private readonly List<(int? Percent, string? Message)> _reports = [];

    public string JobId { get; init; } = Guid.NewGuid().ToString("N");
    public string JobName { get; init; } = "test";
    public int Attempt { get; init; } = 1;
    public string? CorrelationId { get; init; }
    public DateTimeOffset ScheduledFor { get; init; } = DateTimeOffset.UtcNow;
    public string? ScheduleName { get; init; }

    /// <summary>Every call to <see cref="ReportProgressAsync"/>, in order.</summary>
    public IReadOnlyList<(int? Percent, string? Message)> ProgressReports => _reports;

    /// <summary>The last reported percentage.</summary>
    public int? Progress { get; private set; }

    /// <summary>The last reported message.</summary>
    public string? Message { get; private set; }

    public Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        _reports.Add((percent, message));
        Progress = percent ?? Progress;
        Message = message ?? Message;
        return Task.CompletedTask;
    }
}
