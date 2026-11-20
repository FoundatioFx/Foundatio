using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Utility;

namespace Foundatio.Testing;

/// <summary>An <see cref="IJobContext"/> for unit testing a job. Records reported progress.</summary>
/// <example>
/// <code>
/// var context = new TestJobContext();
/// await new GenerateReportJob(...).HandleAsync(new GenerateReport("pdf"), context, CancellationToken.None);
/// Assert.Equal(100, context.Progress);
/// </code>
/// </example>
public sealed class TestJobContext : IJobContext
{
    private readonly List<(int? Percent, string? Message)> _reports = [];

    /// <inheritdoc />
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");
    /// <inheritdoc />
    public string JobName { get; init; } = "test";
    /// <inheritdoc />
    public int Attempt { get; init; } = 1;
    /// <inheritdoc />
    public string? CorrelationId { get; init; }
    /// <inheritdoc />
    public DateTimeOffset ScheduledFor { get; init; } = DateTimeOffset.UtcNow;
    /// <inheritdoc />
    public string? ScheduleName { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Headers { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Every call to <see cref="ReportProgressAsync"/>, in order.</summary>
    public IReadOnlyList<(int? Percent, string? Message)> ProgressReports
    {
        get
        {
            lock (_reports)
                return _reports.ToArray();
        }
    }

    /// <summary>The last reported percentage.</summary>
    public int? Progress { get; private set; }

    /// <summary>The last reported message.</summary>
    public string? Message { get; private set; }

    /// <summary>Records the report in <see cref="ProgressReports"/>, <see cref="Progress"/> and <see cref="Message"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="percent"/> is below 0 or above 100.</exception>
    public Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        HandlerProgress.ThrowIfInvalid(percent);
        lock (_reports)
        {
            _reports.Add((percent, message));
            Progress = percent ?? Progress;
            Message = message ?? Message;
        }

        return Task.CompletedTask;
    }
}
