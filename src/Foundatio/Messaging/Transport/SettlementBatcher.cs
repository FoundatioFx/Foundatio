using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Sends settlements (completes, abandons, renewals) in batches, as a group commit, for a transport whose broker settles
/// several messages in one call. Foundatio settles each message on its own, as soon as its handler returns, and often
/// many at once; a receiver whose <see cref="IMessageReceiver.CompleteAsync"/> calls <see cref="SendAsync"/> turns those
/// into a few calls. An entry is sent at once while no call is in flight, so a lone settlement waits for nothing.
/// Otherwise it waits for the next call, which goes out as soon as a call in flight returns or
/// <see cref="MaxBatchSize"/> entries are waiting. Each caller gets its own entry's result.
/// </summary>
/// <remarks>
/// <para>
/// A caller that cancels before its entry is sent drops the entry. A call already sent is cancelled only once every
/// caller in it has cancelled; until then a cancelled caller still waits for the call, so a settlement it started
/// cannot land after one that follows it.
/// </para>
/// <para>
/// Use one batcher per receiver and kind of call, since a call settles messages of one receiver. Foundatio lets up to
/// a receive batch of settlements wait at once without their handler slots, even with one handler, so a batch can fill.
/// Thread-safe.
/// </para>
/// </remarks>
/// <typeparam name="TEntry">What one settlement sends, such as the message's receipt.</typeparam>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public sealed class SettlementBatcher<TEntry>
{
    private readonly Func<IReadOnlyList<TEntry>, CancellationToken, Task<IReadOnlyList<Exception?>?>> _send;
    private readonly object _lock = new();
    private readonly List<Pending> _waiting = [];
    private int _calls;

    /// <param name="maxBatchSize">The most entries one call sends.</param>
    /// <param name="send">
    /// Sends up to <paramref name="maxBatchSize"/> entries in one call and returns each entry's error, in the order the
    /// entries were given (null where it succeeded), or null when every entry succeeded. An exception it throws fails
    /// every entry of the call. Called concurrently when a batch fills while a call is in flight.
    /// </param>
    public SettlementBatcher(int maxBatchSize, Func<IReadOnlyList<TEntry>, CancellationToken, Task<IReadOnlyList<Exception?>?>> send)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
        ArgumentNullException.ThrowIfNull(send);

        MaxBatchSize = maxBatchSize;
        _send = send;
    }

    /// <summary>The most entries one call sends.</summary>
    public int MaxBatchSize { get; }

    /// <summary>
    /// Sends <paramref name="entry"/> with others waiting at the same time, and completes once its call has returned:
    /// faulted with the entry's error when it failed.
    /// </summary>
    public Task SendAsync(TEntry entry, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        var pending = new Pending(this, entry, cancellationToken);
        Call? call = null;
        lock (_lock)
        {
            if (_calls == 0)
            {
                // Nothing waits while no call is in flight, so the entry goes alone, on its caller's token.
                call = new Call([pending]);
                pending.Call = call;
                _calls++;
            }
            else
            {
                // Run synchronously when the token is already cancelled; the lock is reentrant, and the entry is not waiting yet.
                if (cancellationToken.CanBeCanceled)
                    pending.Registration = cancellationToken.Register(static state => ((Pending)state!).Cancel(), pending);

                if (cancellationToken.IsCancellationRequested)
                {
                    pending.Registration.Dispose();
                    pending.TrySetCanceled(cancellationToken);
                }
                else
                {
                    _waiting.Add(pending);
                    if (_waiting.Count >= MaxBatchSize)
                        call = TakeCallLocked();
                }
            }
        }

        if (call is not null)
            _ = SendCallAsync(call);

        return pending.Task;
    }

    private Call TakeCallLocked()
    {
        var entries = new Pending[Math.Min(_waiting.Count, MaxBatchSize)];
        _waiting.CopyTo(0, entries, 0, entries.Length);
        _waiting.RemoveRange(0, entries.Length);
        var call = new Call(entries);
        foreach (var pending in call.Entries)
            pending.Call = call;

        _calls++;
        return call;
    }

    private async Task SendCallAsync(Call call)
    {
        IReadOnlyList<Exception?>? errors = null;
        Exception? failure = null;
        try
        {
            var entries = new TEntry[call.Entries.Length];
            for (int i = 0; i < entries.Length; i++)
                entries[i] = call.Entries[i].Entry;

            errors = await _send(entries, call.Token).AnyContext();
            if (errors is not null && errors.Count != entries.Length)
                failure = new InvalidOperationException($"The settlement call returned {errors.Count} results for {entries.Length} entries.");
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

        bool cancelled = failure is OperationCanceledException && call.Token.IsCancellationRequested;
        for (int i = 0; i < call.Entries.Length; i++)
        {
            var pending = call.Entries[i];
            pending.Registration.Dispose();
            if (cancelled)
                pending.TrySetCanceled(pending.Token);
            else if (failure is not null)
                pending.TrySetException(failure);
            else if (errors?[i] is { } error)
                pending.TrySetException(error);
            else
                pending.TrySetResult();
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
                    pending.TrySetCanceled(pending.Token);

                return;
            }

            if (!call.Done && --call.Waiting == 0)
                abort = call.Cancellation;
        }

        abort?.Cancel();
    }

    /// <summary>One caller's entry, completed with its result.</summary>
    private sealed class Pending(SettlementBatcher<TEntry> batcher, TEntry entry, CancellationToken token) : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public TEntry Entry { get; } = entry;
        public CancellationToken Token { get; } = token;
        public CancellationTokenRegistration Registration { get; set; }

        /// <summary>The call the entry was sent in, set under the batcher's lock; null while it waits.</summary>
        public Call? Call { get; set; }

        public void Cancel() => batcher.Cancel(this);
    }

    private sealed class Call
    {
        public Call(Pending[] entries)
        {
            Entries = entries;
            Waiting = entries.Length;
            if (entries.Length == 1)
            {
                Token = entries[0].Token;
            }
            else
            {
                Cancellation = new CancellationTokenSource();
                Token = Cancellation.Token;
            }
        }

        public Pending[] Entries { get; }

        /// <summary>
        /// With several callers, cancelled once every one of them has cancelled. Never disposed: it has no timer or
        /// parent. A call of one caller uses that caller's token instead.
        /// </summary>
        public CancellationTokenSource? Cancellation { get; }

        /// <summary>The token the call is sent on.</summary>
        public CancellationToken Token { get; }

        /// <summary>How many callers in the call have not cancelled, read and written under the batcher's lock.</summary>
        public int Waiting { get; set; }

        public bool Done { get; set; }
    }
}
