using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Jobs;

/// <summary>
/// Runs a legacy <see cref="IJob"/>, such as a <see cref="JobBase"/>, as a durable job. Register it with
/// <see cref="FoundatioJobsBuilder.AddLegacyJob{TJob}"/>, enqueue it with <c>EnqueueAsync&lt;LegacyJob&lt;TJob&gt;&gt;()</c>
/// and schedule it with <c>AddCronJob&lt;LegacyJob&lt;TJob&gt;&gt;(...)</c>.
/// </summary>
/// <remarks>
/// The job is named after <typeparamref name="TJob"/>, so stored jobs and schedules keep their names. A successful
/// <see cref="JobResult"/>'s message becomes the job's message. A failed result fails the attempt with its error, or
/// with its message when it has none, and the job's retry policy decides whether it runs again. A cancelled result ends
/// the job as cancelled when its cancellation token fired, and fails the attempt otherwise. Implement
/// <see cref="IDurableJob"/> instead to reach the job's context: its attempt, scheduled occurrence and progress.
/// </remarks>
/// <typeparam name="TJob">The legacy job, resolved from the run's service scope or created with its constructor's services.</typeparam>
public sealed class LegacyJob<TJob> : IDurableJob where TJob : class, IJob
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the adapter for one run.</summary>
    /// <param name="services">The run's service scope, which <typeparamref name="TJob"/> is resolved from.</param>
    public LegacyJob(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public async Task RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var job = ActivatorUtilities.GetServiceOrCreateInstance<TJob>(_services);
        var result = await job.RunAsync(cancellationToken).AnyContext();
        if (result is null || result.IsSuccess)
        {
            if (result?.Message is { Length: > 0 } message)
                await context.ReportProgressAsync(message: message, cancellationToken: CancellationToken.None).AnyContext();

            return;
        }

        if (result.IsCancelled && cancellationToken.IsCancellationRequested)
        {
            if (result.Message is { Length: > 0 } message)
                await context.ReportProgressAsync(message: message, cancellationToken: CancellationToken.None).AnyContext();

            throw new OperationCanceledException(result.Message is { Length: > 0 } reason ? reason : null, result.Error, cancellationToken);
        }

        if (result.Error is { } error)
            ExceptionDispatchInfo.Throw(error);

        throw new JobFailedException(result.Message is { Length: > 0 } failure ? failure : "The job reported failure.");
    }
}
