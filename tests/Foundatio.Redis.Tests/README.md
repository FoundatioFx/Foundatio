# Foundatio.Redis.Tests

Validates the temporary in-repo Redis providers against a real Redis:

- **`RedisJobRuntimeStore`** — runs the shared `JobRuntimeStoreConformanceTests` suite (the same assertions the
  in-memory reference store passes) plus `RedisJobStoreIntegrationTests` (delayed-send fallback + CRON end-to-end).
- **`RedisStreamsMessageTransport`** — runs the shared `MessageTransportConformanceTests` suite (pull, settlement,
  visibility timeout, lock renewal, redelivery delay, dead-letter, provisioning, stats, topic fan-out; push/priority/
  expiration/delayed-delivery skip via capability gates) plus `RedisStreamsTransportIntegrationTests` (cross-instance
  crash recovery, the core's retry/dead-letter machinery over Streams, and `PubSub` fan-out).

## Running

Start Redis and point the tests at it. Without the connection string every test is skipped.

```sh
docker compose -f tests/Foundatio.Redis.Tests/docker-compose.yml up -d

export FOUNDATIO_REDIS_CONNECTION_STRING=localhost:6399
dotnet run --project tests/Foundatio.Redis.Tests
```

Each test runs under a unique key prefix, so concurrent runs and leftover keys never collide. The job-store suite
drives lease/expiry timing with a `FakeTimeProvider` (no real sleeps); the transport suite uses real, whole-second
timing windows (the same cross-transport windows the AWS suite uses).
