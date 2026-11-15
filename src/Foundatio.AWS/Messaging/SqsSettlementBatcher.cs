using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Sends one kind of SQS settlement call for a receiver in batches of up to 10 entries, as a group commit. An entry is
/// sent at once while no call is in flight, so a lone settlement waits for nothing. Otherwise it waits for the next
/// call, which goes out as soon as a call in flight returns or 10 entries are waiting. Each caller gets its own
/// entry's result.
/// </summary>
/// <remarks>
/// A caller that cancels before its entry is sent drops the entry. A call already sent is cancelled only once every
/// caller in it has cancelled, as a single call is today; until then a cancelled caller still waits for the call, so a
/// visibility change it started cannot land after a settlement that follows it.
/// </remarks>
internal sealed class SqsSettlementBatcher<TEntry>
{
    private readonly Func<IReadOnlyList<TEntry>, CancellationToken, Task<Exception?[]>> _send;
    private readonly object _lock = new();
    private readonly List<Pending> _waiting = [];
    private int _calls;

    /// <param name="send">
    /// Sends up to 10 entries in one call and returns each entry's error, or null where it succeeded. An exception it
    /// throws fails every entry of the call.
    /// </param>
    public SqsSettlementBatcher(Func<IReadOnlyList<TEntry>, CancellationToken, Task<Exception?[]>> send)
    {
        _send = send;
    }

    public Task SendAsync(TEntry entry, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        var pending = new Pending(this, entry, cancellationToken);
        if (cancellationToken.CanBeCanceled)
            pending.Registration = cancellationToken.Register(static state => ((Pending)state!).Cancel(), pending);

        Call? call = null;
        lock (_lock)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                pending.Completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                _waiting.Add(pending);
                if (_calls == 0 || _waiting.Count >= SqsMessageTransport.MaxBatchSize)
                    call = TakeCallLocked();
            }
        }

        if (call is not null)
            _ = SendCallAsync(call);

        return pending.Completion.Task;
    }

    private Call TakeCallLocked()
    {
        int count = Math.Min(_waiting.Count, SqsMessageTransport.MaxBatchSize);
        var call = new Call(_waiting.GetRange(0, count));
        _waiting.RemoveRange(0, count);
        foreach (var pending in call.Entries)
            pending.Call = call;

        _calls++;
        return call;
    }

    private async Task SendCallAsync(Call call)
    {
        Exception?[]? errors = null;
        Exception? failure = null;
        try
        {
            var entries = new TEntry[call.Entries.Count];
            for (int i = 0; i < entries.Length; i++)
                entries[i] = call.Entries[i].Entry;

            errors = await _send(entries, call.Cancellation.Token).AnyContext();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Call? next = null;
        lock (_lock)
        {
            call.Done = true;
            _calls--;
            if (_waiting.Count > 0)
                next = TakeCallLocked();
        }

        if (next is not null)
            _ = SendCallAsync(next);

        bool cancelled = failure is OperationCanceledException && call.Cancellation.IsCancellationRequested;
        for (int i = 0; i < call.Entries.Count; i++)
        {
            var pending = call.Entries[i];
            pending.Registration.Dispose();
            if (cancelled)
                pending.Completion.TrySetCanceled(pending.Token);
            else if (failure is not null)
                pending.Completion.TrySetException(failure);
            else if (errors![i] is { } error)
                pending.Completion.TrySetException(error);
            else
                pending.Completion.TrySetResult();
        }
    }

    private void Cancel(Pending pending)
    {
        CancellationTokenSource? abort = null;
        lock (_lock)
        {
            if (pending.Call is not { } call)
            {
                if (_waiting.Remove(pending))
                    pending.Completion.TrySetCanceled(pending.Token);

                return;
            }

            if (!call.Done && --call.Waiting == 0)
                abort = call.Cancellation;
        }

        abort?.Cancel();
    }

    private sealed class Pending(SqsSettlementBatcher<TEntry> batcher, TEntry entry, CancellationToken token)
    {
        public TEntry Entry { get; } = entry;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }

        /// <summary>The call the entry was sent in, set under the batcher's lock; null while it waits.</summary>
        public Call? Call { get; set; }

        public void Cancel() => batcher.Cancel(this);
    }

    private sealed class Call(List<Pending> entries)
    {
        public List<Pending> Entries { get; } = entries;

        /// <summary>Cancelled once every caller in the call has cancelled. Never disposed: it has no timer or parent.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>How many callers in the call have not cancelled, read and written under the batcher's lock.</summary>
        public int Waiting { get; set; } = entries.Count;

        public bool Done { get; set; }
    }
}
