---
name: cross-repo-validation
description: Validate a Foundatio core change across the providers - in this repo (Redis, AWS, Kafka, Azure Service Bus, RabbitMQ) and in their own repos (Azure Storage, Aliyun, SshNet). Use when changing core abstractions, QueueBase/CacheClient/MessageBus behavior, Foundatio.TestHarness base classes, or shared docs, or when the user asks to build, test, push or update PRs across repos.
---

# Cross-repo validation

Core changes are only done when every affected provider builds and passes its tests against the local core source. Run this workflow without being asked whenever a change touches `src/Foundatio`, `src/Foundatio.TestHarness`, or provider-facing docs.

Redis, AWS, Kafka, Azure Service Bus and RabbitMQ live in this repo (`src/Foundatio.Redis`, `src/Foundatio.AWS`, `src/Foundatio.Kafka`, `src/Foundatio.AzureServiceBus`, `src/Foundatio.RabbitMQ`) and already build against core through project references, so validating them doesn't need a second repo:

- Update their tests and code in the same change as core. `dotnet build Foundatio.slnx` builds them.
- Start their services from the repo root with `docker compose up -d && docker compose up ready` (Redis cluster, Kafka, Floci, Service Bus emulator), then run `dotnet test --project tests/Foundatio.Redis.Tests/Foundatio.Redis.Tests.csproj`, and the same for `Foundatio.AWS.Tests`, `Foundatio.Kafka.Tests`, `Foundatio.AzureServiceBus.Tests` and `Foundatio.RabbitMQ.Tests`. The RabbitMQ tests start their own brokers through Aspire and only need Docker. A subagent per test project can run them in parallel once the solution is built; pass `--no-build`.
- There are no preview bumps or separate PRs for them. Steps 2 to 6 below are for the providers that still live in their own repos: Azure Storage, Aliyun and SshNet.

## 1. Find the affected repos

- TestHarness base class signature changes (for example a new `GetQueue` parameter) are breaking: every provider override must be updated. Search the in-repo provider tests and each sibling repo for the overridden member: `rg -n "override .*GetQueue\(" tests ../Foundatio.*/tests`.
- New `virtual` base tests need a `[Fact]`/`[Theory]` override in every provider test class that derives from the base, or they won't run there. Skip only with a concrete reason (`[Fact(Skip = "Legacy mode does not support GroupId")]`).

## 2. Build against local core, once and sequentially

```powershell
$env:ReferenceFoundatioSource = 'true'
foreach ($r in 'Foundatio.AzureStorage','Foundatio.Aliyun','Foundatio.Storage.SshNet') {
    Push-Location "../$r"; dotnet build "$r.slnx"; Pop-Location
}
```

Provider builds compile core into the same `obj` folders, so never build two provider solutions at the same time.

## 3. Start the emulators and wait until they're healthy

- Run `docker compose up -d` in each provider repo and wait for `docker ps` to report `healthy`. In this repo, run `docker compose up -d && docker compose up ready` for Redis, AWS, Kafka and Azure Service Bus; the Service Bus emulator can take 1-2 minutes more to accept connections.
- After editing `build/servicebus-emulator/Config.json`, run `docker compose up -d --force-recreate servicebus-emulator sqledge`. The emulator only reads its config at startup.
- If nearly every test in a repo fails after the same ~5 seconds, the service isn't reachable. Fix the environment before you touch any code.
- Redis cluster on macOS: start it from this repo's root with `$env:HOST_IP = (ipconfig getifaddr en0); docker compose up -d`. Without `HOST_IP`, the nodes advertise `172.17.0.1`, which a Mac can't reach. `start-all-services.ps1` can't work out the IP on macOS.
- These test projects use xUnit v3 on Microsoft.Testing.Platform, where `--filter "FullyQualifiedName~X"` matches nothing. Use `--filter-method "*X*"` instead.

## 4. Run the provider tests in parallel

Give each repo (or pair of small repos) its own subagent so they run at the same time. Tell every subagent to:

- Set `$env:ReferenceFoundatioSource='true'` and run `dotnet test <test csproj> --no-build`.
- After an edit, rebuild only the projects it changed, with `dotnet build <csproj> --no-dependencies`. It must never rebuild core or a whole `.slnx`.
- Edit only its own repo, and report core issues back instead of fixing them.
- Leave git alone: no commit, push, stash, checkout or reset.
- Return the files it changed, the total/passed/failed/skipped counts, and each remaining failure, re-run once to separate flaky from real.

Meanwhile, run core yourself: `dotnet build Foundatio.slnx` and `dotnet test Foundatio.slnx`, which includes the Redis, AWS, Kafka, Azure Service Bus and RabbitMQ tests.

## 5. Pre-push checks

- Put a blank line between test methods. This finds misses in changed files: `rg -n -U '\}\r?\n[ \t]*\[(Fact|Theory)' <files>`.
- Check every changed doc statement against the code: defaults on the options class (not the builder), clamping vs. rejecting, and whether a provider actually supports the feature. Re-read `git diff origin/main...HEAD -- docs .agents`.
- When you change core abstractions, update `.agents/skills/foundatio/SKILL.md`.

## 6. Push and update the PRs

1. Push core first. CI publishes `<version>-preview.<branch>.0.<height>`.
2. Update the `Foundatio` and `Foundatio.TestHarness` package versions in each external provider's `src/*/*.csproj` and `tests/Directory.Build.props` to the new preview. Redis, AWS, Kafka, Azure Service Bus and RabbitMQ need no bump; they ship from this repo.
3. Push the providers, then update the PR descriptions. Call out breaking changes (TestHarness signatures) and behavior changes.
