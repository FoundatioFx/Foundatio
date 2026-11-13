# Monitoring Messages and Jobs

See what your messages and durable jobs are doing across every instance: what is running now and how far it has got, what finished recently and why it failed, rolling counts for every message type and job, how many messages wait in each queue, and how many jobs are in each state. `IWorkMonitor` returns all of it in one call, ready for a dashboard to poll every few seconds. Foundatio provides the API, not a dashboard UI.

Every process tracks its own messages and jobs, whether or not you add monitoring. `AddFoundatioMonitoring` adds `IWorkMonitor` to read that data. With a shared cache, each instance also writes a snapshot of it there, so the monitor shows every instance.

## Setup

### One instance

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseInMemory()
    .AddConsumer<ImportCatalogHandler>());
builder.Services.AddFoundatioJobs(j => j
    .UseInMemory()
    .AddJob<GenerateReportJob>());

builder.Services.AddFoundatioMonitoring();
```

Without a cache, the monitor shows only the process that reads it.

### Every instance, with Redis

Give monitoring an `ICacheClient` that every instance reaches. `UseCache()` takes the one registered in DI:

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(await ConnectionMultiplexer.ConnectAsync("localhost"));
builder.Services.AddSingleton<ICacheClient>(sp => new RedisCacheClient(o => o.ConnectionMultiplexer(sp.GetRequiredService<IConnectionMultiplexer>())));

builder.Services.AddFoundatioMessaging(m => m
    .UseRedis()
    .UseServiceName("catalog")
    .AddConsumer<ImportCatalogHandler>());
builder.Services.AddFoundatioJobs(j => j
    .UseRedis()
    .AddJob<GenerateReportJob>());

builder.Services.AddFoundatioMonitoring(m => m.UseCache());
```

- `UseCache(cache)` takes a cache you create and dispose yourself; `UseCache(sp => ...)` one that monitoring creates and disposes. With `UseCache()` and no `ICacheClient` registered, the host fails to start (in a process without handlers or jobs, reading the monitor throws instead).
- Each instance that handles messages or runs jobs writes its snapshot every `SnapshotInterval` (5 seconds), and sooner, at most about once a second, when work starts to show as active, finishes visibly or reports progress. A snapshot expires after three intervals, so an instance that crashes drops out within 15 seconds. One that shuts down gracefully removes its snapshot at once.
- A failed write is logged and retried; it never affects message handling or jobs.
- A process that registers no handlers and no jobs, such as a dashboard, writes nothing but reads every instance. To show backlog and job counts, it needs messaging and jobs configured on the same transport and store, without handlers.
- Use a cache that all instances share. With `HybridCacheClient` or `InMemoryCacheClient`, each instance reads its own local copy, so the monitor shows stale or partial data.

### Options

```csharp
builder.Services.AddFoundatioMonitoring(m => m
    .UseCache()
    .Configure(o =>
    {
        o.SnapshotInterval = TimeSpan.FromSeconds(10);
        o.ActiveThreshold = TimeSpan.FromSeconds(2);
        o.KeyPrefix = "catalog:monitoring";
    }));
```

| Option | Default | |
|---|---|---|
| `SnapshotInterval` | 5 seconds | How often each instance writes its snapshot. Backlog and job counts are cached this long. |
| `SnapshotExpiry` | 3 × the interval | How long a snapshot lasts; must be longer than the interval. |
| `ActiveThreshold` | 1 second | How long work runs before it shows as active. |
| `RecentCapacity` | 100 | Finished items each instance keeps. |
| `StatsWindow` | 5 minutes | How far back stats reach, in one-minute buckets. |
| `KeyPrefix` | `foundatio:monitoring` | Cache keys: `{prefix}:instances` and `{prefix}:instance:{id}`. |
| `ServiceName` | the messaging service name, then the application name | The service an instance shows under. |

## What is tracked

- **Active work**: each message being handled and each job running, with its id, message type or job name, source, handler, attempt, correlation id, start time and last progress. It shows once it has run for `ActiveThreshold`, or as soon as it reports progress, so the monitor isn't flooded by messages that take a few milliseconds.
- **Recent work**: the last `RecentCapacity` items each instance finished, with their outcome, duration and error. It keeps work that showed as active, and every retry, failure, dead letter and cancellation, however fast.
- **Stats**: rolling counts over `StatsWindow` for each message type on each source, and each job name. They count all work, including messages too fast to show as active: how many succeeded, were retried, failed, were dead-lettered, cancelled or skipped, how many run now, and the average and longest duration.

| Outcome | Messages | Jobs |
|---|---|---|
| `Succeeded` | Every handler returned and the message was completed | The job returned |
| `Retried` | A handler threw or called `Retry`, or a queue had no handler for the type | The job threw and will run again |
| `Failed` | Completing or dead-lettering the message failed, so it will be delivered again | The job used up its attempts, or could not start |
| `DeadLettered` | Moved to the dead-letter queue | — |
| `Cancelled` | Interrupted by shutdown or a lost lease; it is delivered again | Cancelled, or interrupted by shutdown or a lost lease |
| `Skipped` | Completed without a handler: none on the subscription for the type, or it expired | — |

Payloads and headers are never recorded. An error is the exception's type and message, or the dead-letter reason, cut to 1,024 characters; never a stack trace.

## Reporting progress

Message handlers and jobs report progress through the same method, `IHandlerContext.ReportProgressAsync`, which `IMessageContext` and `IJobContext` inherit:

```csharp
public class ImportCatalogHandler(ICatalog catalog) : IMessageHandler<ImportCatalog>
{
    public async Task HandleAsync(ImportCatalog message, IMessageContext context, CancellationToken ct)
    {
        var pages = await catalog.GetPagesAsync(message.Url, ct);
        for (int i = 0; i < pages.Count; i++)
        {
            await catalog.ImportAsync(pages[i], ct);
            await context.ReportProgressAsync((i + 1) * 100 / pages.Count, $"Page {i + 1} of {pages.Count}", ct);
        }
    }
}
```

- Percent is 0 to 100; anything else throws `ArgumentOutOfRangeException`. Pass null to keep the previous value, for example to change only the message.
- Reporting makes the work show as active at once, even below `ActiveThreshold`, and keeps it in recent work when it finishes.
- It is cheap to call in a loop: the monitor always sees the latest values, and the rest is throttled to about four times a second.
- For a message, a report also adds a `progress` event to the handler's trace span and logs at Debug. It never calls the transport: the message's lease is renewed automatically either way. For a job, it is also saved to the job, where `IJobClient.GetAsync` reads it. See [messages](./messages#reporting-progress) and [jobs](./durable-jobs#reporting-progress).

## Reading the monitor

Inject `IWorkMonitor` (namespace `Foundatio.Monitoring`), for example in a minimal API endpoint:

```csharp
app.MapGet("/monitor", (IWorkMonitor monitor, CancellationToken cancellationToken) =>
    monitor.GetOverviewAsync(cancellationToken: cancellationToken));

app.MapGet("/monitor/jobs", (IWorkMonitor monitor, CancellationToken cancellationToken) =>
    monitor.GetActiveAsync(new WorkFilter { Kind = WorkKind.Job }, cancellationToken));
```

| Method | Returns |
|---|---|
| `GetOverviewAsync(filter)` | Everything below in one `WorkOverview`, reading each snapshot once |
| `GetInstancesAsync()` | `WorkInstance`: id, service, machine, process id, start and update times, the sources it receives from and the jobs it runs. `IsCurrent` marks the process reading the monitor |
| `GetActiveAsync(filter)` | `ActiveWork`, oldest first, with the instance running it |
| `GetRecentAsync(filter)` | `CompletedWork`, newest first, with its outcome, duration and error |
| `GetStatsAsync(filter)` | `WorkStats` summed across instances: counts and in-flight work added, average durations weighted by how many runs each instance timed, and the longest duration of any instance |
| `GetBacklogAsync()` | `MessageSourceStats` for every queue and durable subscription the instances receive from |
| `GetJobCountsAsync()` | `JobCounts`: how many jobs of each name the instances run are queued, running, succeeded, failed and cancelled |

`WorkFilter` narrows active work, recent work and stats by `Kind`, `Name` (the message type or job name) and `InstanceId`.

- The process reading the monitor uses its live data. Another instance's data is as old as its last snapshot, up to `SnapshotInterval`.
- Times come from each instance's clock, so compare them with care across machines.
- Backlog and job counts are read from this process's transport and job store, once per source and job name, and cached for `SnapshotInterval`, however many dashboards poll. A source the transport cannot count, or whose count failed (logged as a warning), has null counts; a job name whose counts failed has null counts too.
- To count one source yourself, call `IMessageAdmin.GetStatsAsync(source)`.

## Backlog counts

Each count of a `MessageSourceStats` is approximate, may lag sends and settlements by a few seconds, and is null when the transport cannot tell:

| Count | Meaning |
|---|---|
| `Queued` | Waiting to be delivered now: not delayed and not leased to a receiver. Where `InFlight` is null, it also counts delivered messages until they are settled |
| `Delayed` | Waiting for a delivery time in the future, including retries waiting out their delay |
| `InFlight` | Delivered to a receiver and not yet completed, abandoned or dead-lettered |
| `DeadLettered` | In the source's dead-letter queue |

Instance subscriptions are not counted. A broker that cannot tell two states apart counts a message in the nearest one it can report. What the counts rest on depends on the broker:

| Transport | Where the counts come from |
|---|---|
| In-memory | Exact counts of the process's own queues and subscriptions. |
| Redis | The source's stream and consumer group, its lease, retry and delay sets, and its dead-letter stream, read in one script, so the four counts are exact at the moment of the call. Messages held behind an unsettled message of their ordered group count as queued. |
| SQS | The queue's approximate message attributes (visible, delayed and not visible) and its dead-letter queue's. A retry waiting out its visibility timeout, and a send longer than 15 minutes that the transport holds, are not visible, so they count as in flight. FIFO queues never delay a message, so `Delayed` is null there. AWS documents the attributes as approximate; they can lag by about a minute. |
| Azure Service Bus | The queue's or subscription's runtime properties. Service Bus counts locked messages as active, so messages being handled count as queued and `InFlight` is null. `Delayed` counts a queue's scheduled messages (delayed sends and retries); it is null for subscriptions, whose scheduled messages wait in the topic. The emulator reports no counts, so on it the transport returns null. |
| Kafka | Consumer group lag, read without joining the group: the records after the group's committed offsets on the source's topic (and a subscription's own topic). A received message counts as queued until its offset is committed, about a second after it is settled. `InFlight` and `Delayed` are null: receivers' holdings aren't visible across processes, and delayed messages wait in delay topics that all sources share. `DeadLettered` counts the dead letters not yet replayed. |
| RabbitMQ | Passive declares of the source's queue and its dead-letter queue: their ready messages, current at the moment of the call. AMQP does not report unacknowledged messages, so `InFlight` is null and a received message that is not settled is counted nowhere, unless `ManagementEndpoint` is set: the management API then counts them as in flight, and every count lags by up to its statistics interval (5 seconds). `Delayed` is null: delayed messages wait in delay queues that all sources share. |

## Cost

- **Per message or job**: following one costs about 90 nanoseconds and allocates nothing: a few interlocked updates and two uncontended locks. The tracking fields live in the handler's context, which is allocated anyway; it grew by about 64 bytes. On the in-memory transport with a no-op handler, throughput with tracking was within measurement noise of throughput without it.
- **Per snapshot**: building one takes a pass over the active work, the counters and the recent buffer. Writing it is one `SetAsync` and one `ListAddAsync` per interval per instance, and at most one more a second while work shows as active, finishes visibly or reports progress. With 20 message types, a snapshot is about 6 KB; a recent buffer full of 100 failures with 200-character errors adds about 60 KB, and errors at the 1,024-character cap about 140 KB.
- **Per read**: one `GetListAsync` and one `GetAllAsync` on the cache. Backlog adds one `GetStatsAsync` per source, and job counts five `CountAsync` calls per job name, at most once per interval.
- **Memory**: the recent buffer, and one counter per message type and source or job name with a bucket per minute of the window. Beyond 2,048 counters, more names are counted together as `(other)`, so a flood of unknown message types cannot grow memory without bound.
