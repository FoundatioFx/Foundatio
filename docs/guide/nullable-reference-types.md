# Nullable Reference Types (NRT) Migration

Foundatio has been fully annotated with C# [nullable reference types](https://learn.microsoft.com/en-us/dotnet/csharp/nullable-references) across the core library and all provider repositories. This document describes the public API changes, design decisions, and remaining areas for improvement.

## Interface Return Type Changes

These are **breaking changes** for consumers who must now handle nullable return values. All changes are semantically correct — they represent cases where `null` was always a possible runtime value but wasn't expressed in the type system.

### Lock Provider

| Before | After | Reason |
|--------|-------|--------|
| `Task<ILock> AcquireAsync(...)` | `Task<ILock?> AcquireAsync(...)` | Lock acquisition can fail (timeout, contention) |

### Queue

| Before | After | Reason |
|--------|-------|--------|
| `Task<string> EnqueueAsync(...)` | `Task<string?> EnqueueAsync(...)` | Returns `null` when `Enqueuing` event cancels the operation |
| `Task<IQueueEntry<T>> DequeueAsync(...)` | `Task<IQueueEntry<T>?> DequeueAsync(...)` | Returns `null` on timeout or cancellation |

### File Storage

| Before | After | Reason |
|--------|-------|--------|
| `Task<Stream> GetFileStreamAsync(...)` | `Task<Stream?> GetFileStreamAsync(...)` | Returns `null` when file does not exist |
| `Task<FileSpec> GetFileInfoAsync(...)` | `Task<FileSpec?> GetFileInfoAsync(...)` | Returns `null` when file does not exist |

### Serializer

| Before | After | Reason |
|--------|-------|--------|
| `object Deserialize(...)` | `object? Deserialize(...)` | Deserialized data can be `null` |

### Messaging

| Before | After | Reason |
|--------|-------|--------|
| `string IMessage.UniqueId` | `string? IMessage.UniqueId` | Optional field, not always set |
| `string IMessage.CorrelationId` | `string? IMessage.CorrelationId` | Optional correlation tracking field |
| `Type IMessage.ClrType` | `Type? IMessage.ClrType` | Type may not be resolvable at runtime |
| `object IMessage.GetBody()` | `object? IMessage.GetBody()` | Body deserialization can return `null` |

### Queue Entry

| Before | After | Reason |
|--------|-------|--------|
| `string IQueueEntry.CorrelationId` | `string? IQueueEntry.CorrelationId` | Optional field |
| `Type IQueueEntry.EntryType` | `Type? IQueueEntry.EntryType` | Type resolution can fail |

### Resilience

| Before | After | Reason |
|--------|-------|--------|
| `IResiliencePolicy GetPolicy(...)` | `IResiliencePolicy? GetPolicy(...)` | Can return `null` when `useDefault: false` and policy doesn't exist |

### Parameter Nullability

These parameter changes allow callers to pass `null` where it was already supported at runtime:

| Interface | Parameter Change | Reason |
|-----------|-----------------|--------|
| `ICacheClient.RemoveAllAsync` | `IEnumerable<string>? keys = null` | `null` flushes all keys |
| `IFileStorage.DeleteFilesAsync` | `string? searchPattern = null` | `null` deletes all files |
| `IFileStorage.GetPagedFileListAsync` | `string? searchPattern = null` | `null` lists all files |
| `IMessagePublisher.PublishAsync` | `MessageOptions? options = null` | Options are optional |

### Other Interface Properties

| Before | After | Reason |
|--------|-------|--------|
| `string IHaveSubMetricName.SubMetricName` | `string? IHaveSubMetricName.SubMetricName` | Sub-metric is optional |
| `string IHaveUniqueIdentifier.UniqueIdentifier` | `string? IHaveUniqueIdentifier.UniqueIdentifier` | Identifier is optional |

### Queue Job Interface

| Before | After | Reason |
|--------|-------|--------|
| `ProcessAsync(IQueueEntry<T> queueEntry, ...)` | `ProcessAsync(IQueueEntry<T>? queueEntry, ...)` | Dequeue can return `null`; implementations already handled this |

This is a **breaking change** for implementors of `IQueueJob<T>` who must update their method signature.

## Migration Impact for Consumers

### What You Need to Change

1. **Null checks on return values**: Methods that now return nullable types require null checks:
   ```csharp
   // Before
   var entry = await queue.DequeueAsync(token);
   await entry.CompleteAsync(); // no null check needed

   // After
   var entry = await queue.DequeueAsync(token);
   if (entry is null) return;
   await entry.CompleteAsync();
   ```

2. **IQueueJob<T> implementations**: Update the `ProcessAsync` signature:
   ```csharp
   // Before
   public Task<JobResult> ProcessAsync(IQueueEntry<MyData> queueEntry, CancellationToken ct)

   // After
   public Task<JobResult> ProcessAsync(IQueueEntry<MyData>? queueEntry, CancellationToken ct)
   ```

3. **Nullable property access**: Properties like `IMessage.CorrelationId`, `IMessage.ClrType`, and `IQueueEntry.CorrelationId` are now nullable.

## Known `null!` Patterns (Bandaids)

The following areas use `= null!` as a default value to suppress NRT warnings. These are functional but not ideal — the `required` keyword (C# 11+) would be more correct for properties that must always be set.

### Queue Event Args

All queue event args classes use `= null!` for properties that are always populated by the queue infrastructure but lack constructor enforcement:

- `EnqueuingEventArgs<T>`: `Queue`, `Data`, `Options`
- `EnqueuedEventArgs<T>`: `Queue`, `Entry`
- `DequeuedEventArgs<T>`: `Queue`, `Entry`
- `LockRenewedEventArgs<T>`: `Queue`, `Entry`
- `CompletedEventArgs<T>`: `Queue`, `Entry`
- `AbandonedEventArgs<T>`: `Queue`, `Entry`
- `QueueDeletedEventArgs<T>`: `Queue`

### DTOs and Models

| Class | Properties with `null!` |
|-------|------------------------|
| `FileSpec` | `Path` |
| `Message` | `Type` |
| `WorkItemData` | `WorkItemId`, `Type`, `Data` |
| `InvalidateCache` | `CacheId` |
| `ItemExpiredEventArgs` | `Client`, `Key` |
| `CacheLockReleased` | `Resource`, `LockId` |
| `NextPageResult` | `Files` |

### Internal Infrastructure

| Class | Field/Property with `null!` |
|-------|----------------------------|
| `QueueBehaviorBase<T>` | `_queue` field (set via `Attach()`) |
| `MessageBusBase.Subscriber` | `Type`, `Action` (always set during subscription) |

### DI Service Resolution

`SharedOptions.UseServices()` uses `null!` when assigning services from DI:
```csharp
options.ResiliencePolicyProvider = serviceProvider.GetService<IResiliencePolicyProvider>()!;
```
This is safe because `SharedOptions` properties have fallback defaults in their getters (e.g., `DefaultResiliencePolicyProvider.Instance`), but the `null!` suppresses the warning that `GetService` can return `null`.

### Scoped Wrappers

`ScopedCacheClient`, `ScopedLockProvider`, and `ScopedFileStorage` implement `IHaveResiliencePolicyProvider` by delegating to the inner instance with `null!`:
```csharp
IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider
    => UnscopedCache.GetResiliencePolicyProvider()!;
```
This can return `null` at runtime if the inner instance doesn't implement `IHaveResiliencePolicyProvider`.

### Builder Extension Methods

`FoundatioServicesExtensions` uses `options = null!` as default parameter values:
```csharp
public FoundatioBuilder UseInMemory(InMemoryCacheClientOptions options = null!)
```
This is safe because `UseServices()` handles `null` via `options ??= new TOption()`.

## Provider Repository Patterns

All provider repositories follow the same pattern for options classes with connection strings:

| Provider | Options Property |
|----------|-----------------|
| Redis | `ConnectionMultiplexer = null!`, `Subscriber = null!` |
| Azure Service Bus | `ConnectionString = null!`, `FullyQualifiedNamespace = null!`, `Credential = null!` |
| Azure Storage | `ConnectionString = null!` |
| RabbitMQ | `ConnectionString = null!` |
| AWS | Queue entry `Data = null!` |
| Aliyun | `ConnectionString = null!` |
| Minio | `Endpoint = null!`, `AccessKey = null!`, `SecretKey = null!` |
| SSH.NET | `ConnectionString = null!` |
| Kafka | Options properties |

## Recommendations for Future Improvement

### Use `required` Keyword — Constraints and Tradeoffs

The C# 11 `required` keyword seems like the natural replacement for `null!`, but has two significant constraints:

**1. System.Text.Json enforces `required` during deserialization.** If a `required` property is missing from the JSON payload, `JsonSerializer.Deserialize` throws `JsonException`. This means any type that goes through serialization (queue messages, message bus payloads) would break if the property isn't present in the JSON.

**Types affected (serialized — CANNOT use `required` without breaking wire compat):**

- `WorkItemData` — serialized in queue payloads
- `Message` — serialized for message bus
- `InvalidateCache` — sent over message bus
- `CacheLockReleased` — sent over message bus

**2. `required` is incompatible with `new()` generic constraint.** Classes with `required` members produce CS9040 when used with `where T : new()`. This rules out all options classes that use `SharedOptions`'s `UseServices<TOption>()` extension (`where TOption : SharedOptions, new()`).

**Types affected (generic construction — CANNOT use `required`):**

- All `SharedOptions`-derived options classes
- All provider options (connection strings, credentials, etc.)

**Types safe for `required` (never serialized, never generic-constructed):**

- All queue event args classes (`EnqueuingEventArgs<T>`, `EnqueuedEventArgs<T>`, etc.)
- `ItemExpiredEventArgs`
- `FileSpec` (constructed with object initializers, not deserialized)

::: warning
Adding `required` is a **source breaking change** — existing code that creates these types without setting the required property will fail to compile.
:::

### Make DI Resolution Explicit

Replace `GetService<T>()!` in `SharedOptions.UseServices()` with explicit null handling:

```csharp
var provider = serviceProvider.GetService<IResiliencePolicyProvider>();
if (provider is not null)
    options.ResiliencePolicyProvider = provider;
```

This avoids the `null!` and correctly preserves the fallback defaults.

### Scoped Wrapper Safety

The scoped wrappers (`ScopedCacheClient`, etc.) should handle the case where the inner instance doesn't implement `IHaveResiliencePolicyProvider`:

```csharp
IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider
    => UnscopedCache.GetResiliencePolicyProvider() ?? DefaultResiliencePolicyProvider.Instance;
```
