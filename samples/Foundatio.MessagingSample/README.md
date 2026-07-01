# Foundatio.MessagingSample

A minimal ASP.NET app showing the redesigned Foundatio **messaging** (one bus, two verbs) and **durable jobs** in a
real, scaled-out setup. It runs under Aspire with **3 replicas**, so you can watch the distributed behavior.

The core idea: **handlers are registered with no topology decision — the caller's verb decides delivery.**

- `bus.SendAsync(msg)` — a command / unit of work: exactly **one** instance across the fleet processes it.
- `bus.PublishAsync(msg)` — an event: each subscribing **service** receives one copy (a scaled service's replicas
  compete for it), or **every replica** when the handler opts in with `PerInstance = true`.

What the sample demonstrates:

- **Send (worker queue)** — `POST /orders` calls `bus.SendAsync`; exactly **one** replica processes each order. Scale
  up and the work spreads out.
- **Publish (events)** — `POST /announcements` calls `bus.PublishAsync`; the announcement handler registers with
  `PerInstance = true`, so **every** replica logs each announcement.
- **Durable job** — `POST /reports` submits a job via `IJobClient`; whichever replica's runtime pump claims it runs it.
  Poll `GET /reports/{id}` to watch its status/progress.
- **CRON jobs** — declared with `.Jobs.AddCronJob<T>(cron)` and scheduled automatically; occurrences are deduped
  through the shared runtime store so **scope** decides fan-out:
  - `HeartbeatJob` — Global, every minute → runs on **one** replica per tick (leader/singleton).
  - `RefreshCacheJob` — PerNode, every minute → runs on **every** replica per tick (per-instance maintenance).
  - `SweepStaleOrdersJob` — Global, every 2 minutes → a periodic maintenance sweep on one replica.

Messaging runs on **AWS SQS/SNS** (via a LocalStack container) and durable jobs on **Redis** — all wired from one
clean `AddFoundatio()` chain in [`Program.cs`](Program.cs). Swap `UseAws()` for `UseRedis()` to run messaging on
Redis Streams without touching a single handler.

## Run it (Aspire)

```sh
dotnet run --project samples/Foundatio.AppHost
```

The Aspire dashboard launches Redis + LocalStack and 3 replicas of the service. Open the service endpoint and:

```sh
# fire several orders — watch them load-balance across the 3 replicas' logs
for i in $(seq 1 6); do curl -sX POST <url>/orders -H 'content-type: application/json' -d "{\"product\":\"widget\",\"quantity\":$i}"; done

# publish an announcement — every replica logs it (the handler is PerInstance)
curl -sX POST <url>/announcements -H 'content-type: application/json' -d '{"text":"hello all"}'

# submit a durable job, then poll it
job=$(curl -sX POST <url>/reports | jq -r .jobId); curl -s <url>/reports/$job
```

The per-instance id in each log line (`[abc123] processed order: ...`) makes the distribution obvious.

## Run it standalone (no Aspire)

Swap `UseAws()` for `UseRedis()` in `Program.cs` (or run LocalStack for the AWS transport), point at a Redis
instance, and run:

```sh
ConnectionStrings__Redis=localhost:6399 dotnet run --project samples/Foundatio.MessagingSample
```
