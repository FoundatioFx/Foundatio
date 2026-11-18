---
name: foundatio
description: Use when building .NET applications with Foundatio caches, queues, message buses, locks, jobs, storage, serializers, and resilience policies, or choosing a provider for those abstractions.
---

# Foundatio

Start with the guide for the abstraction you are using. When working from a checkout, read the matching Markdown file under `docs/guide/` for that revision. For published examples, query Context7 with the library ID `/foundatiofx/foundatio`, the interface, and the provider.

| Task | Guide |
| --- | --- |
| Register services and select implementations | [Dependency injection](https://foundatio.dev/guide/dependency-injection) |
| Cache values, lists, or expiring entries | [Caching](https://foundatio.dev/guide/caching) |
| Combine local and distributed caches | [Hybrid cache](https://foundatio.dev/guide/implementations/hybrid-cache) |
| Enqueue, complete, abandon, or retry work | [Queues](https://foundatio.dev/guide/queues) |
| Subscribe to or publish messages | [Messaging](https://foundatio.dev/guide/messaging) |
| Acquire, renew, or release a lease | [Locks](https://foundatio.dev/guide/locks) |
| Run, schedule, or host a job | [Jobs](https://foundatio.dev/guide/jobs) |
| Read or write files | [Storage](https://foundatio.dev/guide/storage) |
| Configure serialization | [Serialization](https://foundatio.dev/guide/serialization) |
| Configure retries and circuit breakers | [Resilience](https://foundatio.dev/guide/resilience) |

Use the in-memory implementations for local tests. Before relying on ordering, deduplication, visibility timeouts, or retry delays in production, check the section for the actual provider. [Provider behavioral gaps](https://foundatio.dev/guide/provider-behavioral-gaps)

For reusable provider contract tests, start in [Foundatio.TestHarness](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.TestHarness). For logging in xUnit v3 tests, use the [xUnit v3 logging fixtures](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Xunit.v3/Logging).
