---
name: foundatio
description: Use when building .NET applications with Foundatio ICacheClient, IQueue<T>, IMessageBus, ILockProvider, IJob, IFileStorage, ISerializer, or IResiliencePolicy, or choosing their implementations.
---

# Foundatio

Start with the guide for the abstraction you are using. When working from a checkout, read the matching Markdown file under `docs/guide/` for that revision. For published examples, query Context7 with the library ID `/foundatiofx/foundatio`, the interface, and the provider.

| Task | Guide |
| --- | --- |
| Register services and select implementations | [Dependency injection](https://foundatio.dev/guide/dependency-injection) |
| Cache values and lists with `ICacheClient` | [Caching](https://foundatio.dev/guide/caching) |
| Combine local and distributed caches | [Hybrid cache](https://foundatio.dev/guide/implementations/hybrid-cache) |
| Dequeue and settle entries with `IQueue<T>` | [Queues](https://foundatio.dev/guide/queues) |
| Subscribe to or publish messages | [Messaging](https://foundatio.dev/guide/messaging) |
| Acquire, renew, or release an `ILock` | [Locks](https://foundatio.dev/guide/locks) |
| Run, schedule, or host a job | [Jobs](https://foundatio.dev/guide/jobs) |
| Read or write files | [Storage](https://foundatio.dev/guide/storage) |
| Configure serialization | [Serialization](https://foundatio.dev/guide/serialization) |
| Configure retries and circuit breakers | [Resilience](https://foundatio.dev/guide/resilience) |

Use the in-memory implementations for local tests. Before relying on ordering, deduplication, visibility timeouts, or retry delays in production, check the section for the actual provider. [Provider behavioral gaps](https://foundatio.dev/guide/provider-behavioral-gaps)

- A computed cache TTL below 5 ms can remove data. A key write removes the key, while a list add removes the supplied members. [Minimum expiration](https://foundatio.dev/guide/caching#minimum-expiration)
- A failed queue job result abandons the entry for retry. A standalone job reports the failure to its caller. Choose the result with the intended retry behavior in mind. [Retry vs permanent failure](https://foundatio.dev/guide/jobs#retry-vs-permanent-failure)
- A scheduled Service Bus retry can change the entry ID when duplicate detection is enabled. Use a stable business ID for idempotency. [Retry delay on abandon](https://foundatio.dev/guide/provider-behavioral-gaps#retry-delay-on-abandon)

For reusable provider contract tests, start in [Foundatio.TestHarness](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.TestHarness). For logging in xUnit v3 tests, use the [xUnit v3 logging fixtures](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Xunit.v3/Logging).

In applications that reference `Exceptionless.DateTimeExtensions`, use its `SafeAdd` and `SafeSubtract` for deadline arithmetic. Foundatio core uses its internal `Foundatio.Utility.DateTimeExtensions.SafeAdd`. Read the clock and measure elapsed time through the injected `TimeProvider`.
