using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio;

/// <summary>
/// What message handlers and jobs have in common: the attempt, the correlation id and headers of the work being
/// handled, and a way to report its progress.
/// </summary>
public interface IHandlerContext
{
    /// <summary>The 1-based attempt.</summary>
    int Attempt { get; }

    /// <summary>Links the work to the request, message or job that started it, or null.</summary>
    string? CorrelationId { get; }

    /// <summary>The headers the work was sent or enqueued with, including the W3C trace context. Empty when there are none.</summary>
    IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// Reports how far the work has got, for monitoring. Calling it makes the work visible in
    /// <see cref="Monitoring.IWorkMonitor"/> even when it finishes quickly. Pass only what changed: a null argument keeps
    /// the previously reported value. It is cheap to call often, because updates are throttled, and safe to call from
    /// parallel branches.
    /// </summary>
    /// <param name="percent">How much is done, from 0 to 100, or null to keep the last value.</param>
    /// <param name="message">What is happening now, or null to keep the last message.</param>
    /// <param name="cancellationToken">Cancels saving the report.</param>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="percent"/> is below 0 or above 100.</exception>
    Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default);
}
