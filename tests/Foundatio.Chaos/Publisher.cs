using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;

namespace Foundatio.Chaos;

/// <summary>
/// Sends a steady mix of messages, each with a unique id and a sequence number, and records each one in the ledger
/// before it is sent (<c>A</c>) and once the send returned (<c>S</c>). A failed send is retried until it succeeds, so
/// a group's messages are always sent in sequence.
/// </summary>
public sealed class Publisher(IMessageClient messages, IJobClient? jobs, Ledger ledger, ChaosSettings settings, ILogger<Publisher> logger)
{
    private readonly string _instance = settings.Get("instance", Guid.NewGuid().ToString("N")[..6]);
    private readonly int _poisonEvery = settings.GetInt("poison-every", 250);
    private readonly int _transientEvery = settings.GetInt("transient-every", 40);
    private readonly int _slowEvery = settings.GetInt("slow-every", 0);
    private readonly int _slowMs = settings.GetInt("slow-ms", 25_000);
    private readonly bool _deliverAt = settings.Get("delay-mode", "delay") == "deliver-at";
    private long _sent;
    private long _failures;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(settings.GetInt("duration", 120));
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(duration);
        ledger.Write("X", "publisher-start", _instance, duration.TotalSeconds);

        var loops = new List<Task>
        {
            RunLoopAsync(Streams.Work, settings.GetDouble("work-rate", 20), SendWorkAsync, stop.Token),
            RunLoopAsync(Streams.Grouped, settings.GetDouble("group-batch-rate", 20), SendGroupedAsync, stop.Token),
            RunLoopAsync(Streams.Delayed, settings.GetDouble("delayed-rate", 10), SendDelayedAsync, stop.Token),
            RunLoopAsync(Streams.Event, settings.GetDouble("event-rate", 20), PublishEventsAsync, stop.Token),
            ReportAsync(stop.Token)
        };

        if (jobs is not null)
            loops.Add(RunLoopAsync(Streams.Job, settings.GetDouble("job-rate", 5), EnqueueJobAsync, stop.Token));

        await Task.WhenAll(loops);
        stop.Dispose();
        ledger.Write("X", "publisher-done", _instance, Interlocked.Read(ref _sent), Interlocked.Read(ref _failures));
        logger.LogInformation("Publisher {Instance} done: {Sent} sent, {Failures} failed sends retried", _instance, _sent, _failures);
    }

    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            logger.LogInformation("Publisher {Instance}: {Sent} sent, {Failures} failed sends retried", _instance, Interlocked.Read(ref _sent), Interlocked.Read(ref _failures));
        }
    }

    /// <summary>Calls <paramref name="send"/> <paramref name="perSecond"/> times a second, one call at a time.</summary>
    private async Task RunLoopAsync(string stream, double perSecond, Func<int, CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        if (perSecond <= 0)
            return;

        var interval = TimeSpan.FromSeconds(1 / perSecond);
        var started = DateTimeOffset.UtcNow;
        int iteration = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var due = started + interval * iteration;
            var wait = due - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            await send(iteration++, cancellationToken);
        }

        ledger.Write("X", "loop-done", stream, iteration);
    }

    private FailMode FailFor(int seq) => seq % _poisonEvery == _poisonEvery - 1
        ? FailMode.Always
        : seq % _transientEvery == _transientEvery / 2 ? FailMode.FirstAttempt : FailMode.None;

    private int _workSeq;

    private Task SendWorkAsync(int _, CancellationToken cancellationToken)
    {
        int batch = settings.GetInt("work-batch", 10);
        var batchMessages = new List<WorkMessage>(batch);
        for (int i = 0; i < batch; i++)
        {
            int seq = _workSeq++;
            int slowMs = _slowEvery > 0 && seq % _slowEvery == _slowEvery - 2 ? _slowMs : 0;
            batchMessages.Add(new WorkMessage($"{_instance}-w-{seq}", seq, FailFor(seq), slowMs));
        }

        return SendUntilConfirmedAsync(Streams.Work, batchMessages.Select(m => (m.Id, "", m.Seq, 0L, m.Fail)).ToList(),
            ct => messages.SendBatchAsync(batchMessages, cancellationToken: ct), cancellationToken);
    }

    private readonly Dictionary<int, int> _groupSeq = [];

    private Task SendGroupedAsync(int iteration, CancellationToken cancellationToken)
    {
        int groups = settings.GetInt("groups", 16);
        int batch = settings.GetInt("group-batch", 5);
        int delayedEvery = settings.GetInt("group-delay-every", 15);
        int groupDelayMs = settings.GetInt("group-delay-ms", 3000);
        int groupIndex = iteration % groups;
        string group = $"{_instance}-g{groupIndex}";
        int start = _groupSeq.GetValueOrDefault(groupIndex);
        _groupSeq[groupIndex] = start + batch;

        bool delayed = delayedEvery > 0 && iteration % delayedEvery == delayedEvery - 1;
        long dueMs = delayed ? Ledger.Now() + groupDelayMs : 0;
        var batchMessages = Enumerable.Range(start, batch)
            .Select(seq => new GroupedMessage($"{group}-{seq}", group, seq, dueMs, seq % _transientEvery == 3 ? FailMode.FirstAttempt : seq % (_poisonEvery * 2) == 7 ? FailMode.Always : FailMode.None))
            .ToList();

        var options = new SendOptions
        {
            GroupKey = group,
            Delay = delayed && !_deliverAt ? TimeSpan.FromMilliseconds(groupDelayMs) : null,
            DeliverAt = delayed && _deliverAt ? DateTimeOffset.FromUnixTimeMilliseconds(dueMs) : null
        };

        return SendUntilConfirmedAsync(Streams.Grouped, batchMessages.Select(m => (m.Id, group, m.Seq, dueMs, m.Fail)).ToList(),
            ct => messages.SendBatchAsync(batchMessages, options, ct), cancellationToken);
    }

    private int _delayedSeq;

    private Task SendDelayedAsync(int _, CancellationToken cancellationToken)
    {
        int seq = _delayedSeq++;
        int minMs = settings.GetInt("delay-min-ms", 1000);
        int maxMs = settings.GetInt("delay-max-ms", 30_000);
        int delayMs = Random.Shared.Next(minMs, maxMs + 1);
        long dueMs = Ledger.Now() + delayMs;
        var message = new DelayedMessage($"{_instance}-d-{seq}", seq, dueMs);
        var options = new SendOptions
        {
            Delay = _deliverAt ? null : TimeSpan.FromMilliseconds(delayMs),
            DeliverAt = _deliverAt ? DateTimeOffset.FromUnixTimeMilliseconds(dueMs) : null
        };

        return SendUntilConfirmedAsync(Streams.Delayed, [(message.Id, "", seq, dueMs, FailMode.None)],
            ct => messages.SendAsync(message, options, ct), cancellationToken);
    }

    private int _eventSeq;

    private Task PublishEventsAsync(int _, CancellationToken cancellationToken)
    {
        int batch = settings.GetInt("event-batch", 5);
        var events = new List<ChaosEvent>(batch);
        for (int i = 0; i < batch; i++)
        {
            int seq = _eventSeq++;
            events.Add(new ChaosEvent($"{_instance}-e-{seq}", seq, FailFor(seq)));
        }

        return SendUntilConfirmedAsync(Streams.Event, events.Select(e => (e.Id, "", e.Seq, 0L, e.Fail)).ToList(),
            ct => messages.PublishBatchAsync(events, cancellationToken: ct), cancellationToken);
    }

    private int _jobSeq;

    private Task EnqueueJobAsync(int _, CancellationToken cancellationToken)
    {
        int seq = _jobSeq++;
        bool delayed = seq % 4 == 0;
        long dueMs = delayed ? Ledger.Now() + Random.Shared.Next(1000, 10_000) : 0;
        int slowMs = _slowEvery > 0 && seq % Math.Max(1, _slowEvery / 10) == 1 ? _slowMs : 0;
        var job = new ChaosJob($"{_instance}-j-{seq}", seq, dueMs, FailFor(seq), slowMs);
        var options = new EnqueueOptions
        {
            JobId = job.Id,
            RunAt = delayed ? DateTimeOffset.FromUnixTimeMilliseconds(dueMs) : null
        };

        return SendUntilConfirmedAsync(Streams.Job, [(job.Id, "", seq, dueMs, job.Fail)],
            async ct => { await jobs!.EnqueueAsync(job, options, ct); }, cancellationToken);
    }

    private async Task SendUntilConfirmedAsync(string stream, IReadOnlyList<(string Id, string Group, int Seq, long DueMs, FailMode Fail)> batch, Func<CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        ledger.WriteMany(batch.Select(m => Ledger.Line("A", stream, m.Id, m.Group, m.Seq, m.DueMs, (int)m.Fail)));
        int attempt = 0;
        while (true)
        {
            try
            {
                // Once a send has started it is finished even when the run ends, so a batch is never left half recorded.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.GetInt("send-timeout-seconds", 60)));
                await send(timeout.Token);
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                attempt++;
                Interlocked.Increment(ref _failures);
                ledger.Write("E", stream, batch[0].Id, attempt, ex.GetType().Name, ex.Message);
                if (attempt == 1 || attempt % 20 == 0)
                    logger.LogWarning(ex, "Send of {Stream} {Id} failed (attempt {Attempt}), retrying", stream, batch[0].Id, attempt);

                if (cancellationToken.IsCancellationRequested && attempt >= settings.GetInt("max-send-attempts-after-stop", 120))
                {
                    ledger.Write("X", "send-abandoned", stream, batch[0].Id);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, 250 * attempt)), CancellationToken.None);
            }
        }

        ledger.WriteMany(batch.Select(m => Ledger.Line("S", stream, m.Id, m.Group, m.Seq, m.DueMs, (int)m.Fail)));
        Interlocked.Add(ref _sent, batch.Count);
    }
}
