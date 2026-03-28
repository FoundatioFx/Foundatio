# NRT Migration Audit — PR Review Comment

## Summary

Full NRT annotation completed across Foundatio core + 11 provider repos (~2,750+ unique warnings fixed). All interface return types, parameters, and DTOs have been reviewed for correctness.

## Breaking Public API Changes (Correct)

These are **semantically correct** — they express nullability that was always present at runtime:

| Interface | Change | Impact |
| ----------- | -------- | -------- |
| `ILockProvider.AcquireAsync` | Returns `Task<ILock?>` | Consumers must null-check lock results |
| `IQueue<T>.EnqueueAsync` | Returns `Task<string?>` | Null when `Enqueuing` event cancels |
| `IQueue<T>.DequeueAsync` | Returns `Task<IQueueEntry<T>?>` | Null on timeout/cancellation |
| `IFileStorage.GetFileStreamAsync` | Returns `Task<Stream?>` | Null when file doesn't exist |
| `IFileStorage.GetFileInfoAsync` | Returns `Task<FileSpec?>` | Null when file doesn't exist |
| `ISerializer.Deserialize` | Returns `object?` | Null values deserialize to null |
| `IMessage.UniqueId/CorrelationId` | Now `string?` | Optional fields |
| `IMessage.ClrType` | Now `Type?` | Type may not resolve |
| `IMessage.GetBody()` | Returns `object?` | Body can be null |
| `IQueueEntry.CorrelationId` | Now `string?` | Optional field |
| `IResiliencePolicyProvider.GetPolicy` | Returns `IResiliencePolicy?` | When useDefault=false |
| `IQueueJob<T>.ProcessAsync` | Parameter `IQueueEntry<T>?` | Implementors must update signature |

## Bandaid `null!` Inventory (~90 occurrences across all repos)

### Core Foundatio (~25 occurrences)

**Queue Event Args** (7 classes, ~15 properties): All use `= null!` instead of `required`

- `EnqueuingEventArgs<T>`, `EnqueuedEventArgs<T>`, `DequeuedEventArgs<T>`, `LockRenewedEventArgs<T>`, `CompletedEventArgs<T>`, `AbandonedEventArgs<T>`, `QueueDeletedEventArgs<T>`

**DTOs**: `FileSpec.Path`, `Message.Type`, `WorkItemData.WorkItemId/Type/Data`, `InvalidateCache.CacheId`, `CacheLockReleased.Resource/LockId`, `ItemExpiredEventArgs.Client/Key`, `NextPageResult.Files`

**DI Resolution**: `SharedOptions.UseServices()` — 8x `GetService<T>()!` where services may not be registered

**Scoped Wrappers**: 3x `GetResiliencePolicyProvider()!` in `ScopedCacheClient`, `ScopedLockProvider`, `ScopedFileStorage`

**Builder Extensions**: 5x `options = null!` default parameters in `UseInMemory`/`UseFolder`

### Provider Repos (~68 occurrences)

Primarily options classes with `ConnectionString = null!`, `ConnectionMultiplexer = null!`, `Credential = null!`, etc.

## Low-Risk Fixes Applied

### SharedOptions.UseServices() — DI Resolution (8 `null!` removed)

Replaced `serviceProvider.GetService<T>()!` with null-aware pattern:

```csharp
// Before (passes null via null!)
options.Serializer = serviceProvider.GetService<ISerializer>()!;

// After (preserves fallback defaults in getter)
var serializer = serviceProvider.GetService<ISerializer>();
if (serializer is not null)
    options.Serializer = serializer;
```

This correctly preserves the fallback defaults in `SharedOptions` getters (e.g., `DefaultResiliencePolicyProvider.Instance`).

### Scoped Wrapper Resilience Providers (3 `null!` removed)

Added `?? DefaultResiliencePolicyProvider.Instance` fallback in `ScopedCacheClient`, `ScopedLockProvider`, and `ScopedFileStorage`.

### Lock Provider Constructors (3 `null!` removed)

`CacheLockProvider`, `ThrottlingLockProvider`, and `ThrottlingLockProviderFactory` now use `?? DefaultResiliencePolicyProvider.Instance` instead of `null!`.

### HybridCacheClient (2 `null!` removed)

Fixed `_resiliencePolicyProvider!` interface implementation and `localCacheOptions` initialization.

**Total `null!` removed: 16**

## `required` Keyword Research

### Constraints Discovered

1. **System.Text.Json enforces `required` during deserialization** — `JsonSerializer.Deserialize` throws `JsonException` if a `required` property is missing from JSON. This rules out `required` for serialized types like `WorkItemData`, `InvalidateCache`, `CacheLockReleased`, `Message`.

2. **`required` is incompatible with `new()` generic constraint** — produces CS9040 compile error. This rules out `required` for all `SharedOptions`-derived classes (all provider options).

### What CAN Use `required` (Future Work)

Only types that are never serialized AND never generic-constructed:

- Queue event args (`EnqueuingEventArgs<T>`, `EnqueuedEventArgs<T>`, etc.)
- `ItemExpiredEventArgs`
- `FileSpec`

## Verdict

The NRT migration is **functionally correct**. All interface changes accurately represent runtime nullability. The `null!` patterns are bandaids that work but could be improved — the highest-value fix is adopting `required` for DTOs and event args, which should be done as part of a major version bump.
