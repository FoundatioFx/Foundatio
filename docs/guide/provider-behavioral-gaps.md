# Provider Behavioral Gaps

This document catalogs known behavioral differences across Foundatio provider implementations. While all providers implement the same interfaces, underlying infrastructure limitations mean some operations behave differently than the interface contract might suggest. Use this as a compatibility reference when choosing or switching providers.

## Summary

| Interface | InMemory | Redis | Azure | AWS | RabbitMQ | Kafka | Minio | Aliyun | SshNet |
|-----------|----------|-------|-------|-----|----------|-------|-------|--------|--------|
| `IFileStorage` | Full | Full | Full | Partial | — | — | Partial | Full | Full |
| `IQueue<T>` | Full | Partial | Partial | Partial | — | — | — | — | — |
| `IMessageBus` | Full | Full | Full | Partial | Full | Full | — | — | — |
| `ICacheClient` | Full | Full | — | — | — | — | — | — | — |
| `ILockProvider` | Full | Full | — | — | — | — | — | — | — |

**Legend:** Full = all interface behaviors work as documented. Partial = some operations diverge (see below). — = not implemented by this provider.

---

## IFileStorage

### DeleteFileAsync for Non-Existent Files

| Provider | Behavior | Notes |
|----------|----------|-------|
| InMemory | Returns `false` | ✅ Expected |
| Redis | Returns `false` | ✅ Expected |
| Azure Blob | Returns `false` | ✅ Expected |
| S3 | Returns `true` | ⚠️ S3 DELETE is idempotent by design |
| Minio | Returns `true` | ⚠️ S3-compatible behavior |
| Aliyun | Returns `false` | ✅ Expected |
| SshNet | Returns `false` | ✅ Expected |

**Impact:** Code that uses the return value to determine whether a file existed before deletion will get incorrect results on S3/Minio.

### CopyFileAsync / RenameFileAsync with Non-Existent Source

| Provider | Behavior | Notes |
|----------|----------|-------|
| InMemory | Returns `false` | ✅ Expected |
| Redis | Returns `false` | ✅ Expected |
| Azure Blob | Returns `false` | ✅ Expected |
| S3 | Throws `AmazonS3Exception` | ⚠️ Exception instead of `false` |
| Minio | Returns `false` | ✅ Expected |
| Aliyun | Returns `false` | ✅ Expected |
| SshNet | Returns `false` | ✅ Expected |

**Impact:** Callers must wrap S3 copy/rename in try-catch if the source file might not exist.

### GetFileStreamAsync with Non-Existent File (ReadMode)

| Provider | Behavior | Notes |
|----------|----------|-------|
| InMemory | Returns `null` | ✅ Expected |
| Redis | Returns `null` | ✅ Expected |
| Azure Blob | Returns `null` | ✅ Expected |
| S3 | Throws `AmazonS3Exception` | ⚠️ Exception instead of `null` |
| Minio | Returns `null` | ✅ Expected |
| Aliyun | Returns `null` | ✅ Expected |
| SshNet | Returns `null` | ✅ Expected |

### GetFileStreamAsync with StreamMode.Write

| Provider | Supported | Notes |
|----------|-----------|-------|
| InMemory | ✅ | Full support |
| Azure Blob | ✅ | Full support |
| Aliyun | ✅ | Full support |
| SshNet | ✅ | Full support |
| Redis | ❌ | Not supported |
| S3 | ❌ | Not supported |
| Minio | ❌ | Not supported |

---

## IQueue\<T\>

### GetDeadletterItemsAsync

| Provider | Supported | Notes |
|----------|-----------|-------|
| InMemory | ✅ | Returns all deadlettered entries |
| Redis | ❌ | Not implemented |
| Azure Service Bus | ❌ | Deadletter is a separate sub-queue; requires different API |
| Azure Storage Queue | ❌ | No deadletter concept in Azure Storage Queues |
| SQS | ❌ | Deadletter is a separate queue; not retrievable via this API |

### QueueEntryOptions.UniqueId

| Provider | Supported | Notes |
|----------|-----------|-------|
| InMemory | ✅ | Uses provided ID as the entry ID |
| Redis | ✅ | Uses provided ID as the entry ID |
| Azure Service Bus | ✅ | Maps to MessageId |
| Azure Storage Queue | ❌ | Azure assigns its own MessageId |
| SQS | ❌ | SQS assigns its own MessageId |

### Delivery Delay (DelayUntilUtc)

| Provider | Supported | Granularity | Notes |
|----------|-----------|-------------|-------|
| InMemory | ✅ | Millisecond | Precise delay via background timer |
| Redis | ❌ | — | Not supported |
| Azure Storage Queue | ✅ | Second | `VisibilityTimeout` parameter |
| Azure Service Bus | ✅ | Second | `ScheduledEnqueueTimeUtc` |
| SQS | ✅ | Second (0-900s max) | `DelaySeconds` parameter |

---

## IMessageBus

### Delayed Message Delivery

| Provider | Supported | Granularity | Notes |
|----------|-----------|-------------|-------|
| InMemory | ✅ | Millisecond | Background timer, precise |
| Redis | ✅ | Millisecond | Lua script with delay |
| Azure Service Bus | ✅ | Second | Scheduled enqueue |
| RabbitMQ | ✅ | Millisecond | Via delayed-message-exchange plugin |
| Kafka | ✅ | Millisecond | Client-side delay before produce |
| SQS/SNS | ⚠️ | Second | Impractical due to polling latency |

**Note:** For distributed providers, delay precision is limited by network latency and polling intervals. Tests use 1-second minimum delay to accommodate provider-level granularity.

### Message Ordering Guarantees

| Provider | Ordered | Notes |
|----------|---------|-------|
| InMemory | ✅ | FIFO within a single subscriber |
| Redis | ✅ | Pub/sub delivers in publish order |
| Azure Service Bus | ✅ | FIFO with sessions enabled |
| RabbitMQ | ✅ | FIFO per queue |
| Kafka | ✅ | FIFO per partition |
| SQS/SNS | ⚠️ | Standard queues are best-effort ordering |

---

## ICacheClient

All tested providers (InMemory, Redis) exhibit fully consistent behavior. No behavioral gaps were found for:

- `AddAsync` (returns `false` when key exists)
- `GetAsync` (returns no-value for missing keys)
- `RemoveAsync` (returns `false` for non-existent keys)
- `RemoveByPrefixAsync` (returns 0 for no matches)
- `IncrementAsync` (with zero amount returns current value)
- `SetExpirationAsync` (no-op for non-existent keys)

---

## ILockProvider

All tested providers (InMemory, Redis) exhibit fully consistent behavior. No behavioral gaps were found for:

- `AcquireAsync` with `releaseOnDispose: false`
- `ReleaseAsync` with force release
- `ILock` metadata properties (`LockId`, `Resource`, `AcquiredTimeUtc`)
- `TryUsingAsync` execution and automatic release

---

## Recommendations

1. **Don't rely on `DeleteFileAsync` return value for existence checks** on S3-compatible storage. Use `GetFileInfoAsync` first if you need to know whether a file existed.

2. **Wrap S3 copy/rename/read operations in try-catch** if the source file might not exist. Other providers return `false`/`null` gracefully.

3. **Use at least 1-second granularity for delivery delays** when targeting distributed providers. Sub-second delays are only reliable with InMemory.

4. **Don't rely on `GetDeadletterItemsAsync`** — only InMemory supports it. Design deadletter processing around provider-specific mechanisms instead.

5. **Prefer `QueueEntryOptions.UniqueId` only with providers that support it** (InMemory, Redis, Azure Service Bus). On SQS and Azure Storage Queues, the system assigns its own IDs.
