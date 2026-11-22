#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;

namespace Foundatio;

/// <summary>
/// The concrete transports and job stores implement the provider SPI explicitly, so it isn't public API of theirs.
/// These let provider tests call it on a concrete instance without a cast.
/// </summary>
internal static class TransportSpiExtensions
{
    public static Task SendAsync(this IMessageTransport transport, MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
        => transport.SendAsync(destination, messages, cancellationToken);

    public static Task<IMessageReceiver> CreateReceiverAsync(this IMessageTransport transport, MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
        => transport.CreateReceiverAsync(source, options, cancellationToken);

    public static Task DeleteAsync(this IMessageTransport transport, MessageSource source, CancellationToken cancellationToken = default)
        => transport.DeleteAsync(source, cancellationToken);

    public static Task<MessageSourceStats?> GetStatsAsync(this IMessageTransport transport, MessageSource source, CancellationToken cancellationToken = default)
        => transport.GetStatsAsync(source, cancellationToken);

    public static Task<bool> CreateAsync(this IJobStore store, JobInfo job, CancellationToken cancellationToken = default) => store.CreateAsync(job, cancellationToken);
    public static Task<bool> CreateAsync(this IJobStore store, JobInfo job, TimeSpan availableAfter, CancellationToken cancellationToken = default) => store.CreateAsync(job, availableAfter, cancellationToken);
    public static Task<JobInfo?> GetAsync(this IJobStore store, string jobId, CancellationToken cancellationToken = default) => store.GetAsync(jobId, cancellationToken);
    public static Task<JobPage<JobInfo>> QueryAsync(this IJobStore store, JobQuery query, CancellationToken cancellationToken = default) => store.QueryAsync(query, cancellationToken);
    public static Task<long> CountAsync(this IJobStore store, JobState? state = null, string? name = null, CancellationToken cancellationToken = default) => store.CountAsync(state, name, cancellationToken);
    public static Task<JobInfo?> ClaimAsync(this IJobStore store, JobClaimRequest request, CancellationToken cancellationToken = default) => store.ClaimAsync(request, cancellationToken);
    public static Task<IReadOnlyList<JobInfo>> ClaimBatchAsync(this IJobStore store, JobClaimRequest request, int maxCount, CancellationToken cancellationToken = default) => store.ClaimBatchAsync(request, maxCount, cancellationToken);
    public static Task<JobLeaseStatus> RenewAsync(this IJobStore store, string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => store.RenewAsync(jobId, claimToken, leaseDuration, cancellationToken);
    public static Task<bool> ReportProgressAsync(this IJobStore store, string jobId, string claimToken, int? percent, string? message, CancellationToken cancellationToken = default) => store.ReportProgressAsync(jobId, claimToken, percent, message, cancellationToken);
    public static Task<bool> CompleteAsync(this IJobStore store, string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default) => store.CompleteAsync(jobId, claimToken, completion, cancellationToken);
    public static Task<bool> RequestCancellationAsync(this IJobStore store, string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default) => store.RequestCancellationAsync(jobId, retainFor, cancellationToken);
    public static Task<bool> DeleteAsync(this IJobStore store, string jobId, CancellationToken cancellationToken = default) => store.DeleteAsync(jobId, cancellationToken);
}
