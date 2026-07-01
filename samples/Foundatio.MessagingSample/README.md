# Foundatio.MessagingSample

A minimal ASP.NET app that shows the redesigned Foundatio **messaging** (queues + pub/sub) and **durable jobs** in a
real, scaled-out setup. It runs under Aspire with **3 replicas**, so you can watch the distributed behavior:

- **Queue (competing consumers)** — `POST /orders` enqueues work; exactly **one** replica processes each order. Scale
  up and the work spreads out.
- **Pub/Sub (fan-out)** — `POST /announcements` publishes to a topic; **every** replica receives its own copy (each
  uses a per-instance subscription).
- **Durable job** — `POST /reports` submits a job; whichever replica's runtime pump claims it runs it. Poll
  `GET /reports/{id}` to watch its status/progress.
- **CRON jobs** — scheduled recurring work, deduped through the shared runtime store so **scope** decides fan-out:
  - `heartbeat` — Global, every minute → runs on **one** replica per tick (leader/singleton).
  - `refresh-cache` — PerNode, every minute → runs on **every** replica per tick (per-instance maintenance).
  - `sweep-stale-orders` — Global, every 2 minutes → a periodic maintenance sweep on one replica.

Messaging runs on **AWS SQS/SNS** (via a LocalStack container) and durable jobs on **Redis** — both transports wired
from one clean `AddFoundatio()` chain in [`Program.cs`](Program.cs). The transport is selected by `Messaging:Provider`
(`Aws` or `Redis`), so you can flip it without touching any queue/pub-sub code.

## Run it (Aspire)

```sh
dotnet run --project samples/Foundatio.AppHost
```

The Aspire dashboard launches Redis + LocalStack and 3 replicas of the service. Open the service endpoint and:

```sh
# fire several orders — watch them load-balance across the 3 replicas' logs
for i in $(seq 1 6); do curl -sX POST <url>/orders -H 'content-type: application/json' -d "{\"product\":\"widget\",\"quantity\":$i}"; done

# publish an announcement — every replica logs it
curl -sX POST <url>/announcements -H 'content-type: application/json' -d '{"text":"hello all"}'

# submit a durable job, then poll it
job=$(curl -sX POST <url>/reports | jq -r .jobId); curl -s <url>/reports/$job
```

The per-instance id in each log line (`[abc123] processed order: ...`) makes the distribution obvious. To run messaging
on Redis Streams instead of AWS, set `Messaging__Provider=Redis` on the service in the AppHost.

## Run it standalone (no Aspire)

Point it at a Redis instance (defaults to `localhost:6399`) and use the Redis transport:

```sh
Messaging__Provider=Redis ConnectionStrings__Redis=localhost:6399 \
  dotnet run --project samples/Foundatio.MessagingSample
```
