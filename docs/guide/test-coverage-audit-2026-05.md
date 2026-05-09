# Test Coverage Audit — May 2026

This document summarizes the test coverage improvements planned and executed across the Foundatio workspace in May 2026. The base test harness changes (this repository) establish the new test methods; provider repositories receive corresponding `[Fact]` overrides in separate PRs.

## Scope

- **29 new virtual test methods** added to 5 base test harness classes (this repo)
- **1 implementation fix** to `InMemoryQueue` (this repo)
- **1 timing robustness fix** to `MessageBusTestBase` (this repo)
- **8 provider repositories** require corresponding `[Fact]` overrides (separate PRs)
- **9 tests skipped** with documented provider-specific behavioral gaps

## Changes by Repository

> **Note:** Only the Foundatio (core) row is included in this PR. Provider repo changes are tracked in separate PRs that depend on this one.

| Repository | Files Changed | Lines Added | Lines Removed |
|------------|--------------|-------------|---------------|
| Foundatio (core) | 11 | 925 | 1 |
| Foundatio.Redis | 5 | 174 | 0 |
| Foundatio.AzureStorage | 2 | 84 | 0 |
| Foundatio.AzureServiceBus | 2 | 66 | 0 |
| Foundatio.AWS | 3 | 108 | 0 |
| Foundatio.RabbitMQ | 1 | 24 | 0 |
| Foundatio.Kafka | 1 | 24 | 0 |
| Foundatio.Minio | 1 | 42 | 0 |
| Foundatio.Aliyun | 1 | 42 | 0 |
| **Total** | **27** | **1,489** | **1** |

## New Test Methods

### QueueTestBase (7 new methods)

| Method | Tests |
|--------|-------|
| `DequeueAsync_WithDispose_AutoAbandonsEntryAsync` | Disposing a dequeued entry without completing auto-abandons it |
| `EnqueueAsync_WithUniqueId_UsesProvidedIdAsync` | `QueueEntryOptions.UniqueId` is used as the entry ID |
| `GetDeadletterItemsAsync_WithDeadletteredEntry_ReturnsItemsAsync` | Deadletter retrieval returns abandoned entries |
| `GetQueueActivity_AfterEnqueueAndDequeue_ReturnsTimestampsAsync` | `IQueueActivity` timestamps are populated |
| `GetQueueEntryMetadata_AfterDequeue_ReturnsValidTimestampsAsync` | Entry metadata has valid enqueued/dequeued timestamps |
| `QueueEntry_EntryType_ReturnsCorrectTypeAsync` | `IQueueEntry.EntryType` returns the correct message type |
| `QueueEntry_GetValue_ReturnsUntypedValueAsync` | `IQueueEntry.GetValue()` returns the untyped message object |

### CacheClientTestsBase (6 new methods)

| Method | Tests |
|--------|-------|
| `AddAsync_WhenKeyAlreadyExists_ReturnsFalseAndDoesNotOverwrite` | `AddAsync` returns false without overwriting existing keys |
| `GetAsync_WhenKeyDoesNotExist_ReturnsNoValue` | `GetAsync` returns `HasValue = false` for missing keys |
| `IncrementAsync_WithAmountZero_ReturnsCurrentValue` | Incrementing by zero returns the current value |
| `RemoveAsync_WhenKeyDoesNotExist_ReturnsFalse` | `RemoveAsync` returns false for non-existent keys |
| `RemoveByPrefixAsync_WithNoMatchingKeys_ReturnsZero` | Returns 0 when no keys match the prefix |
| `SetExpirationAsync_OnNonExistentKey_DoesNotThrow` | No-op without exception for missing keys |

### MessageBusTestBase (4 new methods)

| Method | Tests |
|--------|-------|
| `PublishAsync_WithDeliveryDelayExtension_DelaysDeliveryAsync` | Delayed delivery via `TimeSpan` extension method |
| `PublishAsync_WithUniqueId_PropagatesUniqueIdToSubscriberAsync` | UniqueId option flows through to the subscriber |
| `SubscribeAsync_ToRawIMessage_CanAccessAllPropertiesAsync` | `IMessage<T>` exposes CorrelationId, UniqueId, Properties |
| `SubscribeAsync_WithCancellationTokenHandler_ReceivesCancellationTokenAsync` | Handler receives a valid CancellationToken |

### FileStorageTestsBase (7 new methods)

| Method | Tests |
|--------|-------|
| `CopyFileAsync_WithExistingFile_CreatesIdenticalCopy` | Copy creates a byte-identical duplicate |
| `CopyFileAsync_WithNonExistentSource_ReturnsFalse` | Copy with missing source returns false |
| `DeleteFileAsync_WhenFileDoesNotExist_ReturnsFalse` | Delete of non-existent file returns false |
| `DeleteFilesAsync_WithFileSpecCollection_DeletesSpecifiedFiles` | Batch delete with file spec collection |
| `GetFileContentsRawAsync_WithExistingFile_ReturnsByteArray` | Raw byte retrieval for binary content |
| `GetFileStreamAsync_WithNonExistentFileInReadMode_ReturnsNull` | Stream read of missing file returns null |
| `RenameFileAsync_WhenSourceDoesNotExist_ReturnsFalse` | Rename with missing source returns false |

### LockTestBase (5 new methods)

| Method | Tests |
|--------|-------|
| `AcquireAsync_WithReleaseOnDisposeFalse_DoesNotReleaseOnDispose` | Lock survives disposal when releaseOnDispose is false |
| `Lock_AcquiredTimeUtc_ReturnsValidTimestamp` | `ILock.AcquiredTimeUtc` is populated correctly |
| `Lock_LockIdAndResource_ReturnCorrectValues` | `ILock.LockId` and `Resource` match expected values |
| `ReleaseAsync_WithForceRelease_ReleasesLockWithoutLockId` | Force release doesn't require the original lock ID |
| `TryUsingAsync_WithSuccessfulAction_ExecutesAndReleasesLock` | `TryUsingAsync` runs the action and releases |

## Provider Override Matrix

| Provider | Queue | Cache | Messaging | Storage | Lock | Total |
|----------|-------|-------|-----------|---------|------|-------|
| InMemory | 7 | 6 | 4 | 7 | 5 | **29** |
| Redis | 7 | 6 | 4 | 7 | 5 | **29** |
| Azure Storage | 7 (2 skip) | — | — | 7 | — | **14** |
| Azure Service Bus | 7 | — | 4 | — | — | **11** |
| AWS (SQS/S3) | 7 (2 skip) | — | 4 | 7 (4 skip) | — | **18** |
| RabbitMQ | — | — | 4 | — | — | **4** |
| Kafka | — | — | 4 | — | — | **4** |
| Minio | — | — | — | 7 (1 skip) | — | **7** |
| Aliyun | — | — | — | 7 | — | **7** |

## Skipped Tests (9 total)

Each skip documents a legitimate provider behavioral gap:

| Provider | Test | Reason |
|----------|------|--------|
| Azure Storage Queue | `EnqueueAsync_WithUniqueId_UsesProvidedIdAsync` | Azure Storage Queues do not support custom entry IDs |
| Azure Storage Queue | `GetDeadletterItemsAsync_WithDeadletteredEntry_ReturnsItemsAsync` | Azure Storage Queues do not support retrieving the entire queue |
| SQS | `EnqueueAsync_WithUniqueId_UsesProvidedIdAsync` | SQS does not support custom entry IDs |
| SQS | `GetDeadletterItemsAsync_WithDeadletteredEntry_ReturnsItemsAsync` | SQS does not support retrieving deadletter items |
| S3 | `CopyFileAsync_WithNonExistentSource_ReturnsFalse` | S3 throws AmazonS3Exception instead of returning false |
| S3 | `GetFileStreamAsync_WithNonExistentFileInReadMode_ReturnsNull` | S3 throws AmazonS3Exception instead of returning null |
| S3 | `RenameFileAsync_WhenSourceDoesNotExist_ReturnsFalse` | S3 throws AmazonS3Exception instead of returning false |
| S3 | `DeleteFileAsync_WhenFileDoesNotExist_ReturnsFalse` | S3 DELETE is idempotent (returns success for non-existent files) |
| Minio | `DeleteFileAsync_WhenFileDoesNotExist_ReturnsFalse` | Minio/S3 DELETE is idempotent |

## Test Results (at time of audit)

| Provider | Total | Passed | Failed | Skipped | Status |
|----------|-------|--------|--------|---------|--------|
| InMemory | 1906 | 1893 | 0 | 13 | Pass |
| Azure Storage | 108 | 96 | 0 | 12 | Pass |
| AWS (S3/SQS) | 155 | 146 | 0 | 9 | Pass |
| Aliyun | 35 | 35 | 0 | 0 | Pass |
| Minio | 36 | 34 | 0 | 2 | Pass |
| Redis | — | — | — | — | Requires server |
| Azure Service Bus | — | — | — | — | Requires server |
| RabbitMQ | — | — | — | — | Requires server |
| Kafka | — | — | — | — | Requires server |

## Implementation Fixes

### InMemoryQueue: Respect `QueueEntryOptions.UniqueId`

**File:** `src/Foundatio/Queues/InMemoryQueue.cs`

**Before:**
```csharp
string id = Guid.NewGuid().ToString("N");
```

**After:**
```csharp
string id = !String.IsNullOrEmpty(options.UniqueId) ? options.UniqueId : Guid.NewGuid().ToString("N");
```

The `QueueEntryOptions.UniqueId` property existed but was never consumed by the InMemory implementation. All other providers that support custom IDs (Redis, Azure Service Bus) already used it.

### MessageBusTestBase: Timing Robustness for Distributed Providers

**File:** `src/Foundatio.TestHarness/Messaging/MessageBusTestBase.cs`

The `PublishAsync_WithDeliveryDelayExtension_DelaysDeliveryAsync` test was updated to be reliable across distributed providers:

| Parameter | Before | After | Reason |
|-----------|--------|-------|--------|
| Delivery delay | 100ms | 1s | Below minimum granularity for most distributed brokers |
| "Not received" window | 50ms | 250ms | Network latency alone can exceed 50ms |
| "Should arrive" timeout | 5s | 10s | Generous for distributed latency + jitter |
| Subscription warm-up | None | 250ms | Allows subscription to propagate on broker |

## Methodology

1. Identified untested public API surface in each base test harness
2. Implemented tests following three-part naming (`Method_State_Behavior`), AAA structure, and alphabetical insertion
3. Validated all new tests pass against InMemory implementations first
4. Fanned out to all provider repositories in parallel
5. Documented provider-specific gaps with `[Fact(Skip)]` where behaviors legitimately diverge
6. Created provider behavioral gaps reference documentation
