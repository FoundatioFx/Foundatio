using System.Globalization;
using System.Text;
using System.Text.Json;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Chaos;

/// <summary>
/// Reads every ledger in a run directory, waits until what was sent has been handled (or the wait runs out), then
/// checks the delivery guarantees and asks the broker for what is left: backlog counts and dead letters.
/// </summary>
public sealed class Verifier(IServiceProvider services, ChaosSettings settings)
{
    private sealed record Sent(string Stream, string Id, string Group, int Seq, long DueMs, FailMode Fail, long AttemptedAt, long? ConfirmedAt);

    private sealed class Run(string file, string stream, string id, int attempt, long begin, string deliveryId)
    {
        public string DeliveryId { get; } = deliveryId;
        public string File { get; } = file;
        public string Stream { get; } = stream;
        public string Id { get; } = id;
        public int Attempt { get; } = attempt;
        public long Begin { get; } = begin;
        public long? End { get; set; }
        public string? Outcome { get; set; }
    }

    private sealed class Snapshot
    {
        public Dictionary<string, Sent> Sent { get; } = [];
        public List<Run> Runs { get; } = [];
        public Dictionary<string, long> LastLineByFile { get; } = [];
        public List<(long Ts, string File, string Text)> Notes { get; } = [];
        public List<(long Ts, string File, long Rss, long Heap, int Fds, int Threads, int Running, int Failing)> Metrics { get; } = [];
        public int SendErrors { get; set; }
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        string runDirectory = settings.RunDirectory;
        var waitUntil = DateTimeOffset.UtcNow.AddSeconds(settings.GetInt("wait", 300));
        int quietSeconds = settings.GetInt("quiet-seconds", 10);
        Snapshot snapshot;
        long lastRunCount = -1;
        var quietSince = DateTimeOffset.UtcNow;
        while (true)
        {
            snapshot = Load(runDirectory);
            int outstanding = Outstanding(snapshot).Count;
            if (snapshot.Runs.Count != lastRunCount)
            {
                lastRunCount = snapshot.Runs.Count;
                quietSince = DateTimeOffset.UtcNow;
            }

            bool quiet = DateTimeOffset.UtcNow - quietSince >= TimeSpan.FromSeconds(quietSeconds);
            if ((outstanding == 0 && quiet) || DateTimeOffset.UtcNow >= waitUntil)
                break;

            Console.WriteLine($"[verify] {outstanding} confirmed messages not yet settled as expected; waiting");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        var report = new StringBuilder();
        bool failed = await AnalyzeAsync(snapshot, report, cancellationToken);
        string text = report.ToString();
        Console.WriteLine(text);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, settings.Get("report", "report.txt")), text, cancellationToken);
        return failed ? 1 : 0;
    }

    private static Snapshot Load(string runDirectory)
    {
        var snapshot = new Snapshot();
        var openRuns = new Dictionary<(string, string), Run>();
        foreach (string file in Directory.GetFiles(runDirectory, "*.tsv").Order())
        {
            string name = Path.GetFileNameWithoutExtension(file);
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            while (reader.ReadLine() is { } line)
            {
                if (!line.EndsWith('\t') && line.Length == 0)
                    continue;

                string[] f = line.Split('\t');
                if (f.Length < 2 || !Int64.TryParse(f[0], out long ts))
                    continue;

                snapshot.LastLineByFile[name] = ts;
                try
                {
                    switch (f[1])
                    {
                        case "A":
                            if (!snapshot.Sent.ContainsKey(f[3]))
                                snapshot.Sent[f[3]] = new Sent(f[2], f[3], f[4], Int(f[5]), Int64.Parse(f[6], CultureInfo.InvariantCulture), (FailMode)Int(f[7]), ts, null);
                            break;
                        case "S":
                            snapshot.Sent[f[3]] = snapshot.Sent.TryGetValue(f[3], out var sent)
                                ? sent with { ConfirmedAt = sent.ConfirmedAt ?? ts }
                                : new Sent(f[2], f[3], f[4], Int(f[5]), Int64.Parse(f[6], CultureInfo.InvariantCulture), (FailMode)Int(f[7]), ts, ts);
                            break;
                        case "E":
                            snapshot.SendErrors++;
                            break;
                        case "B":
                            var run = new Run(name, f[2], f[3], Int(f[4]), ts, f.Length > 6 ? f[6] : "");
                            snapshot.Runs.Add(run);
                            openRuns[(name, f[5])] = run;
                            break;
                        case "F":
                            if (openRuns.Remove((name, f[5]), out var begun))
                            {
                                begun.End = ts;
                                begun.Outcome = f[4];
                            }
                            break;
                        case "M":
                            snapshot.Metrics.Add((ts, name, Int64.Parse(f[2], CultureInfo.InvariantCulture), Int64.Parse(f[3], CultureInfo.InvariantCulture), Int(f[4]), Int(f[5]), Int(f[9]), Int(f[10])));
                            break;
                        case "X":
                            snapshot.Notes.Add((ts, name, String.Join(' ', f.Skip(2))));
                            break;
                    }
                }
                catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException)
                {
                    // A process killed mid-write leaves a partial last line.
                }
            }
        }

        return snapshot;
    }

    private static int Int(string value) => Int32.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>Confirmed messages that are not settled as expected yet: no successful run, or a poison message without its last attempt.</summary>
    private List<Sent> Outstanding(Snapshot snapshot)
    {
        int maxAttempts = settings.GetInt("max-attempts", 3);
        var ok = snapshot.Runs.Where(r => r.Outcome == "ok").Select(r => r.Id).ToHashSet();
        var finalThrow = snapshot.Runs.Where(r => r.Outcome == "throw" && r.Attempt >= maxAttempts).Select(r => r.Id).ToHashSet();
        return snapshot.Sent.Values
            .Where(s => s.ConfirmedAt is not null)
            .Where(s => s.Fail == FailMode.Always ? !finalThrow.Contains(s.Id) : !ok.Contains(s.Id))
            .ToList();
    }

    private async Task<bool> AnalyzeAsync(Snapshot snapshot, StringBuilder report, CancellationToken cancellationToken)
    {
        bool failed = false;
        int maxAttempts = settings.GetInt("max-attempts", 3);
        var transport = services.GetRequiredService<IMessageTransport>();
        bool ordered = settings.GetBool("expect-ordered", transport.Capabilities.OrderedGroups);
        long earlyToleranceMs = settings.GetInt("early-tolerance-ms", 0);
        // Messages sent before this time may be lost by design, such as by a broker restart without persistence.
        long expectedLossUntil = settings.GetLong("expected-loss-until-ms", 0);
        var runsById = snapshot.Runs.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Begin).ToList());

        var deadLetters = await ReadDeadLettersAsync(report, cancellationToken);

        report.AppendLine(CultureInfo.InvariantCulture, $"# Chaos run {Path.GetFileName(settings.RunDirectory)}: {settings.Transport}");
        report.AppendLine(CultureInfo.InvariantCulture, $"ordered groups expected: {ordered}; send errors retried: {snapshot.SendErrors}; ledgers: {snapshot.LastLineByFile.Count}");
        report.AppendLine();
        report.AppendLine("| stream | attempted | confirmed | unconfirmed handled | handled ok | lost (+ expected) | dup (extra ok runs; of them sent twice) | redelivered after kill/lease | dead-lettered expected / found | unexpected DLQ | early | lateness p50 / p99 / max ms | e2e p50 / p99 / max ms |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        var lostIds = new List<string>();
        foreach (var stream in snapshot.Sent.Values.GroupBy(s => s.Stream).OrderBy(g => g.Key))
        {
            var all = stream.ToList();
            var confirmed = all.Where(s => s.ConfirmedAt is not null).ToList();
            var unconfirmed = all.Where(s => s.ConfirmedAt is null).ToList();
            int unconfirmedHandled = unconfirmed.Count(s => runsById.ContainsKey(s.Id));
            int handledOk = 0, lost = 0, expectedLost = 0, dupRuns = 0, sentTwice = 0, redelivered = 0, dlqExpected = 0, dlqFound = 0, early = 0;
            var lateness = new List<long>();
            var endToEnd = new List<long>();
            foreach (var sent in confirmed)
            {
                var runs = runsById.GetValueOrDefault(sent.Id) ?? [];
                int okRuns = runs.Count(r => r.Outcome == "ok");
                if (okRuns > 0)
                    handledOk++;
                if (okRuns > 1)
                {
                    dupRuns += okRuns - 1;
                    int copies = runs.Select(r => r.DeliveryId).Distinct().Count();
                    if (copies > 1)
                        sentTwice++;
                }
                redelivered += runs.Count(r => r.Outcome is null or "cancel");

                if (sent.Fail == FailMode.Always)
                {
                    dlqExpected++;
                    bool found = sent.Stream == Streams.Job ? runs.Count(r => r.Outcome == "throw") >= maxAttempts : deadLetters.Contains(sent.Id);
                    if (found)
                        dlqFound++;
                    else if (sent.AttemptedAt <= expectedLossUntil)
                        expectedLost++;
                    else
                    {
                        lost++;
                        lostIds.Add(sent.Id + " (dead letter missing)");
                    }
                }
                else if (okRuns == 0 && sent.AttemptedAt <= expectedLossUntil)
                {
                    expectedLost++;
                }
                else if (okRuns == 0)
                {
                    lost++;
                    lostIds.Add(sent.Id + (deadLetters.Contains(sent.Id) ? " (dead-lettered)" : runs.Count > 0 ? $" ({runs.Count} runs, none ok)" : " (never handled)"));
                }

                if (runs.Count > 0)
                {
                    long firstBegin = runs[0].Begin;
                    if (sent.DueMs > 0)
                    {
                        if (firstBegin < sent.DueMs - earlyToleranceMs)
                            early++;
                        lateness.Add(firstBegin - sent.DueMs);
                    }
                    else
                    {
                        endToEnd.Add(firstBegin - sent.AttemptedAt);
                    }
                }
            }

            int unexpectedDlq = confirmed.Count(s => s.Fail != FailMode.Always && deadLetters.Contains(s.Id));
            if (lost > 0 || early > 0 || unexpectedDlq > 0)
                failed = true;

            report.AppendLine(CultureInfo.InvariantCulture, $"| {stream.Key} | {all.Count} | {confirmed.Count} | {unconfirmedHandled}/{unconfirmed.Count} | {handledOk} | {lost} (+{expectedLost}) | {dupRuns}; {sentTwice} | {redelivered} | {dlqExpected} / {dlqFound} | {unexpectedDlq} | {early} | {Percentiles(lateness)} | {Percentiles(endToEnd)} |");
        }

        var knownIds = snapshot.Sent.Keys.ToHashSet();
        int strangers = runsById.Keys.Count(id => !knownIds.Contains(id));
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"handled ids never sent: {strangers}");
        if (strangers > 0)
            failed = true;

        if (lostIds.Count > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"lost ({lostIds.Count}, first 30): {String.Join(", ", lostIds.Take(30))}");
        }

        failed |= CheckOverlaps(snapshot, runsById, report);
        if (ordered)
            failed |= CheckOrder(snapshot, runsById, report, maxAttempts);

        ReportStalls(snapshot, report);
        ReportMetrics(snapshot, report);
        failed |= await ReportBacklogAsync(report, cancellationToken);

        report.AppendLine();
        report.AppendLine(failed ? "RESULT: FAIL" : "RESULT: PASS");
        return failed;
    }

    private static string Percentiles(List<long> values)
    {
        if (values.Count == 0)
            return "-";

        values.Sort();
        long At(double p) => values[Math.Min(values.Count - 1, (int)(p * values.Count))];
        return $"{At(0.5)} / {At(0.99)} / {values[^1]}";
    }

    /// <summary>
    /// Two runs of one delivered message (same transport message id) at the same time: a lease was lost or a
    /// redelivery ran beside the original. Copies the publisher sent twice after a failed send have different ids.
    /// </summary>
    private static bool CheckOverlaps(Snapshot snapshot, Dictionary<string, List<Run>> runsById, StringBuilder report)
    {
        var overlaps = new List<string>();
        foreach (var runs in runsById.Values.SelectMany(r => r.GroupBy(x => x.DeliveryId)).Select(g => g.ToList()))
        {
            string id = runs[0].Id;
            for (int i = 1; i < runs.Count; i++)
            {
                var previous = runs[i - 1];
                long previousEnd = previous.End ?? snapshot.LastLineByFile.GetValueOrDefault(previous.File, Int64.MaxValue);
                if (runs[i].Begin < previousEnd)
                    overlaps.Add($"{id} attempt {previous.Attempt}@{previous.File} [{previous.Begin}-{previous.End?.ToString(CultureInfo.InvariantCulture) ?? "killed"} {previous.Outcome}] overlaps attempt {runs[i].Attempt}@{runs[i].File} by {previousEnd - runs[i].Begin} ms");
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"concurrent runs of one message: {overlaps.Count}");
        foreach (string overlap in overlaps.Take(20))
            report.AppendLine("  - " + overlap);

        return overlaps.Count > 0;
    }

    /// <summary>Within a group, a message must not start before its predecessor's run that settled it has finished.</summary>
    private static bool CheckOrder(Snapshot snapshot, Dictionary<string, List<Run>> runsById, StringBuilder report, int maxAttempts)
    {
        var violations = new List<string>();
        int checkedPairs = 0;
        foreach (var group in snapshot.Sent.Values.Where(s => s.Group.Length > 0 && s.ConfirmedAt is not null).GroupBy(s => s.Group))
        {
            var bySeq = group.OrderBy(s => s.Seq).ToList();
            for (int i = 1; i < bySeq.Count; i++)
            {
                var previous = bySeq[i - 1];
                var current = bySeq[i];
                if (previous.Seq != current.Seq - 1)
                    continue;

                var previousRuns = runsById.GetValueOrDefault(previous.Id);
                var currentRuns = runsById.GetValueOrDefault(current.Id);
                if (previousRuns is null || currentRuns is null)
                    continue;

                var settledBy = previous.Fail == FailMode.Always
                    ? previousRuns.Where(r => r.Outcome == "throw" && r.Attempt >= maxAttempts).OrderBy(r => r.End).FirstOrDefault()
                    : previousRuns.Where(r => r.Outcome == "ok").OrderBy(r => r.End).FirstOrDefault();
                if (settledBy?.End is not { } settledAt)
                    continue;

                checkedPairs++;
                long currentBegin = currentRuns[0].Begin;
                if (currentBegin < settledAt)
                    violations.Add($"{current.Id} began {settledAt - currentBegin} ms before {previous.Id} was settled ({currentRuns[0].File} / {settledBy.File})");
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"group order: {checkedPairs} consecutive pairs checked, {violations.Count} out of order");
        foreach (string violation in violations.Take(20))
            report.AppendLine("  - " + violation);

        return violations.Count > 0;
    }

    /// <summary>Seconds in which nothing began while sent messages were waiting, and how long after each fault work resumed.</summary>
    private static void ReportStalls(Snapshot snapshot, StringBuilder report)
    {
        if (snapshot.Runs.Count == 0)
            return;

        var begins = snapshot.Runs.Select(r => r.Begin).Order().ToList();
        var stalls = new List<(long From, long To)>();
        for (int i = 1; i < begins.Count; i++)
        {
            if (begins[i] - begins[i - 1] >= 3000)
                stalls.Add((begins[i - 1], begins[i]));
        }

        long origin = snapshot.Sent.Values.Select(s => s.AttemptedAt).DefaultIfEmpty(begins[0]).Min();
        report.AppendLine(CultureInfo.InvariantCulture, $"gaps of 3 s or more with no handler starting (t=s from first send): {stalls.Count}");
        foreach (var (from, to) in stalls.Take(20))
            report.AppendLine(CultureInfo.InvariantCulture, $"  - t={(from - origin) / 1000.0:F1}s to {(to - origin) / 1000.0:F1}s ({(to - from) / 1000.0:F1}s)");

        foreach (var note in snapshot.Notes.Where(n => n.Text.StartsWith("fault", StringComparison.Ordinal) || n.Text.StartsWith("heal", StringComparison.Ordinal)).OrderBy(n => n.Ts))
        {
            long? nextOk = snapshot.Runs.Where(r => r.Outcome == "ok" && r.Begin >= note.Ts).Select(r => (long?)r.Begin).Min();
            report.AppendLine(CultureInfo.InvariantCulture, $"  {note.Text} at t={(note.Ts - origin) / 1000.0:F1}s; next handler start after it: +{(nextOk - note.Ts) / 1000.0:F1}s");
        }
    }

    private static void ReportMetrics(Snapshot snapshot, StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("| process | samples | RSS MB first / max / last | managed MB max | fds first / max / last | threads first / max / last | processors failing (max) |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var process in snapshot.Metrics.GroupBy(m => m.File).OrderBy(g => g.Key))
        {
            var samples = process.OrderBy(m => m.Ts).ToList();
            report.AppendLine(CultureInfo.InvariantCulture, $"| {process.Key} | {samples.Count} | {samples[0].Rss / 1048576} / {samples.Max(m => m.Rss) / 1048576} / {samples[^1].Rss / 1048576} | {samples.Max(m => m.Heap) / 1048576} | {samples[0].Fds} / {samples.Max(m => m.Fds)} / {samples[^1].Fds} | {samples[0].Threads} / {samples.Max(m => m.Threads)} / {samples[^1].Threads} | {samples.Max(m => m.Failing)} |");
        }
    }

    private async Task<HashSet<string>> ReadDeadLettersAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        var admin = services.GetRequiredService<IMessageAdmin>();
        var ids = new HashSet<string>();
        foreach (var source in Sources())
        {
            try
            {
                var deadLetters = await admin.PeekDeadLettersAsync(source, settings.GetInt("dead-letter-peek", 5000), cancellationToken);
                foreach (var deadLetter in deadLetters)
                {
                    if (ReadId(deadLetter.Body) is { } id && !ids.Add(id))
                        report.AppendLine(CultureInfo.InvariantCulture, $"dead letter {id} appears more than once in {source}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"peeking dead letters of {source} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return ids;
    }

    private static string? ReadId(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (String.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                    return property.Value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static IEnumerable<MessageSource> Sources() =>
    [
        MessageSource.Queue(Streams.WorkQueue),
        MessageSource.Queue(Streams.GroupedQueue),
        MessageSource.Queue(Streams.DelayedQueue),
        MessageSource.Subscription(Streams.EventTopic, Streams.ServiceName)
    ];

    private async Task<bool> ReportBacklogAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        bool stuck = false;
        var admin = services.GetRequiredService<IMessageAdmin>();
        report.AppendLine();
        report.AppendLine("| source | queued | delayed | in flight | dead-lettered |");
        report.AppendLine("|---|---|---|---|---|");
        foreach (var source in Sources())
        {
            try
            {
                var stats = await admin.GetStatsAsync(source, cancellationToken);
                report.AppendLine(CultureInfo.InvariantCulture, $"| {source} | {stats?.Queued?.ToString(CultureInfo.InvariantCulture) ?? "?"} | {stats?.Delayed?.ToString(CultureInfo.InvariantCulture) ?? "?"} | {stats?.InFlight?.ToString(CultureInfo.InvariantCulture) ?? "?"} | {stats?.DeadLettered?.ToString(CultureInfo.InvariantCulture) ?? "?"} |");
                if (stats?.Queued > 0 || stats?.InFlight > 0 || stats?.Delayed > 0)
                    stuck = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"| {source} | stats failed: {ex.GetType().Name}: {ex.Message} | | | |");
            }
        }

        if (services.GetService<IJobClient>() is { } jobs)
        {
            var counts = new List<string>();
            foreach (var state in Enum.GetValues<JobState>())
            {
                try
                {
                    long count = await jobs.CountAsync(state, "chaos.job", cancellationToken);
                    counts.Add($"{state}={count}");
                    if (state is JobState.Queued or JobState.Running && count > 0)
                        stuck = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    counts.Add($"{state}=error {ex.GetType().Name}");
                }
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"jobs: {String.Join(", ", counts)}");
        }

        if (stuck)
            report.AppendLine("backlog left on the broker after the run (stuck or uncounted messages)");

        return stuck;
    }
}
