# Foundatio.Redis.Tests

Validates the temporary in-repo `RedisJobRuntimeStore` against a real Redis by running the shared
`JobRuntimeStoreConformanceTests` suite (the same assertions the in-memory reference store passes).

## Running

Start Redis and point the tests at it. Without the connection string every test is skipped.

```sh
docker compose -f tests/Foundatio.Redis.Tests/docker-compose.yml up -d

export FOUNDATIO_REDIS_CONNECTION_STRING=localhost:6399
dotnet run --project tests/Foundatio.Redis.Tests
```

Each test runs under a unique key prefix (`fnd-conf:{guid}:`), so concurrent runs and leftover keys never collide.
A `FakeTimeProvider` drives lease/expiry timing, so the suite is fast and deterministic — no real sleeps.
