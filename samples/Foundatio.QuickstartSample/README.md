# Foundatio quickstart

Messaging and durable jobs with no external dependencies.

```bash
dotnet run                 # then POST /orders, POST /reports, GET /reports/{id}, GET /monitor
dotnet run -- --verify     # runs the flow once and exits
```

- `Contracts.cs`: an event, a command and job arguments, each with a stable name.
- `Handlers.cs`: an event subscriber that sends a command, and the command's consumer, which reports progress.
- `Jobs.cs`: a durable job with progress, and a CRON job.
- `Program.cs`: registration, three endpoints, and `/monitor`, which shows active and recent work, stats, queue backlog and job counts.

To run several instances, add `Foundatio.Redis`, register an `IConnectionMultiplexer` and replace both `UseInMemory()` calls with `UseRedis()`. Register a `RedisCacheClient` as `ICacheClient` and call `AddFoundatioMonitoring(m => m.UseCache())`, so `/monitor` shows every instance. Nothing else changes. See [Transports](../../docs/guide/messages.md#transports) for SQS and Kafka.
