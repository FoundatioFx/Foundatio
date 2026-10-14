---
name: cross-repo-validation
description: Validate a Foundatio core change across the provider repos (Redis, AWS, Azure Storage, Azure Service Bus, RabbitMQ, Kafka, Aliyun, SshNet). Use when changing core abstractions, QueueBase/CacheClient/MessageBus behavior, Foundatio.TestHarness base classes, or shared docs, or when the user asks to build, test, push or update PRs across repos.
---

# Cross-repo validation

Core changes are only done when every affected provider builds and passes its tests against the local core source. Run this workflow without being asked whenever a change touches `src/Foundatio`, `src/Foundatio.TestHarness`, or provider-facing docs.

## 1. Find the affected repos

- TestHarness base class signature changes (for example a new `GetQueue` parameter) are breaking: every provider override must be updated. Search each sibling repo for the overridden member: `rg -n "override .*GetQueue\(" ../Foundatio.*/tests`.
- New `virtual` base tests need a `[Fact]`/`[Theory]` override in every provider test class that derives from the base, or they won't run there. Skip only with a concrete reason (`[Fact(Skip = "Legacy mode does not support GroupId")]`).

## 2. Build against local core, once and sequentially

```powershell
$env:ReferenceFoundatioSource = 'true'
foreach ($r in 'Foundatio.Redis','Foundatio.AWS','Foundatio.AzureStorage','Foundatio.AzureServiceBus') {
    Push-Location "../$r"; dotnet build "$r.slnx"; Pop-Location
}
```

Provider builds compile core into the same `obj` folders, so never build two provider solutions at the same time.

## 3. Start the emulators and wait until they're healthy

- Run `docker compose up -d` in each provider repo and wait for `docker ps` to report `healthy` (the Service Bus emulator takes 1-2 minutes).
- After editing `ServiceBus-Emulator/Config/Config.json`, run `docker compose down` and then `docker compose up -d`. The emulator only reads its config at startup.
- If nearly every test in a repo fails after the same ~5 seconds, the service isn't reachable. Fix the environment before you touch any code.
- Redis cluster on macOS: start it with `$env:HOST_IP = (ipconfig getifaddr en0); docker compose up -d`. Without `HOST_IP`, the nodes advertise `172.17.0.1`, which a Mac can't reach. `start-services.ps1` can't work out the IP on macOS.
- These test projects use xUnit v3 on Microsoft.Testing.Platform, where `--filter "FullyQualifiedName~X"` matches nothing. Use `--filter-method "*X*"` instead.

## 4. Run the provider tests in parallel

Give each repo (or pair of small repos) its own subagent so they run at the same time. Tell every subagent to:

- Set `$env:ReferenceFoundatioSource='true'` and run `dotnet test <test csproj> --no-build`.
- After an edit, rebuild only the projects it changed, with `dotnet build <csproj> --no-dependencies`. It must never rebuild core or a whole `.slnx`.
- Edit only its own repo, and report core issues back instead of fixing them.
- Leave git alone: no commit, push, stash, checkout or reset.
- Return the files it changed, the total/passed/failed/skipped counts, and each remaining failure, re-run once to separate flaky from real.

Meanwhile, run core yourself: `dotnet build Foundatio.slnx` and `dotnet test Foundatio.slnx`.

## 5. Pre-push checks

- Put a blank line between test methods. This finds misses in changed files: `rg -n -U '\}\r?\n[ \t]*\[(Fact|Theory)' <files>`.
- Check every changed doc statement against the code: defaults on the options class (not the builder), clamping vs. rejecting, and whether a provider actually supports the feature. Re-read `git diff origin/main...HEAD -- docs .agents`.
- When you change core abstractions, update `.agents/skills/foundatio/SKILL.md`.

## 6. Push and update the PRs

1. Push core first. CI publishes `<version>-preview.<branch>.0.<height>`.
2. Update the `Foundatio` and `Foundatio.TestHarness` package versions in each provider's `src/*/*.csproj` and `tests/Directory.Build.props` to the new preview.
3. Push the providers, then update the PR descriptions. Call out breaking changes (TestHarness signatures) and behavior changes.
