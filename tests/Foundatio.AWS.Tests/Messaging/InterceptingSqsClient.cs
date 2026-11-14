using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace Foundatio.AWS.Tests.Messaging;

/// <summary>
/// Wraps a real SQS client to record its requests, let a test replace or alter any call, and apply AWS rules the
/// emulator does not enforce.
/// </summary>
public class InterceptingSqsClient : DispatchProxy
{
    private const int MaxVisibilityTimeoutSeconds = 12 * 60 * 60;
    private static readonly MethodInfo CastMethod = typeof(InterceptingSqsClient).GetMethod(nameof(CastAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly ConcurrentDictionary<string, DateTime> _receivedAt = new(StringComparer.Ordinal);
    private int _rejectedVisibilityChanges;

    public IAmazonSQS Inner { get; private set; } = null!;

    /// <summary>
    /// Rejects a visibility change that would keep a delivery invisible for more than 12 hours from its receive, as
    /// real SQS does with <c>InvalidParameterValue</c>.
    /// </summary>
    public bool EnforceVisibilityLimit { get; set; }

    /// <summary>Called for every request; call the function it is given to send the request on to the real client.</summary>
    public Func<AmazonWebServiceRequest, Func<Task<AmazonWebServiceResponse>>, Task<AmazonWebServiceResponse>>? Intercept { get; set; }

    public ConcurrentQueue<AmazonWebServiceRequest> Requests { get; } = new();

    public int RejectedVisibilityChanges => Volatile.Read(ref _rejectedVisibilityChanges);

    public IEnumerable<T> RequestsOf<T>() where T : AmazonWebServiceRequest => Requests.OfType<T>();

    public static IAmazonSQS Create(IAmazonSQS inner, out InterceptingSqsClient interceptor)
    {
        var client = Create<IAmazonSQS, InterceptingSqsClient>();
        interceptor = (InterceptingSqsClient)(object)client;
        interceptor.Inner = inner;
        return client;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (args is not [AmazonWebServiceRequest request, ..] || !targetMethod.ReturnType.IsGenericType || targetMethod.ReturnType.GetGenericTypeDefinition() != typeof(Task<>))
            return InvokeInner(targetMethod, args);

        Requests.Enqueue(request);
        var response = SendAsync(request, async () =>
        {
            var task = (Task)InvokeInner(targetMethod, args)!;
            await task;
            return (AmazonWebServiceResponse)task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task)!;
        });

        return CastMethod.MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0]).Invoke(null, [response]);
    }

    private async Task<AmazonWebServiceResponse> SendAsync(AmazonWebServiceRequest request, Func<Task<AmazonWebServiceResponse>> send)
    {
        if (EnforceVisibilityLimit && request is ChangeMessageVisibilityRequest change && ExceedsVisibilityLimit(change.ReceiptHandle, change.VisibilityTimeout))
        {
            Interlocked.Increment(ref _rejectedVisibilityChanges);
            throw new AmazonSQSException($"Value {change.VisibilityTimeout} for parameter VisibilityTimeout is invalid. Reason: Total VisibilityTimeout for the message is beyond the limit [{MaxVisibilityTimeoutSeconds} seconds].")
            {
                ErrorCode = "InvalidParameterValue"
            };
        }

        if (EnforceVisibilityLimit && request is ChangeMessageVisibilityBatchRequest batch && batch.Entries.Any(e => ExceedsVisibilityLimit(e.ReceiptHandle, e.VisibilityTimeout)))
        {
            var rejected = batch.Entries.Where(e => ExceedsVisibilityLimit(e.ReceiptHandle, e.VisibilityTimeout)).ToList();
            Interlocked.Add(ref _rejectedVisibilityChanges, rejected.Count);
            batch.Entries = batch.Entries.Except(rejected).ToList();
            var accepted = batch.Entries.Count > 0 ? (ChangeMessageVisibilityBatchResponse)await SendOnAsync(request, send) : new ChangeMessageVisibilityBatchResponse();
            accepted.Failed = [.. accepted.Failed ?? [], .. rejected.Select(e => new BatchResultErrorEntry { Id = e.Id, Code = "InvalidParameterValue", SenderFault = true })];
            return accepted;
        }

        var response = await SendOnAsync(request, send);
        if (response is ReceiveMessageResponse received)
        {
            foreach (var message in received.Messages ?? [])
                _receivedAt[message.ReceiptHandle] = DateTime.UtcNow;
        }

        return response;
    }

    private Task<AmazonWebServiceResponse> SendOnAsync(AmazonWebServiceRequest request, Func<Task<AmazonWebServiceResponse>> send) =>
        Intercept is { } intercept ? intercept(request, send) : send();

    private bool ExceedsVisibilityLimit(string receiptHandle, int? visibilityTimeout) =>
        _receivedAt.TryGetValue(receiptHandle, out var receivedAt) && (DateTime.UtcNow - receivedAt).TotalSeconds + visibilityTimeout.GetValueOrDefault() > MaxVisibilityTimeoutSeconds;

    private object? InvokeInner(MethodInfo method, object?[]? args)
    {
        try
        {
            return method.Invoke(Inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static async Task<T> CastAsync<T>(Task<AmazonWebServiceResponse> response) => (T)(object)await response;
}
