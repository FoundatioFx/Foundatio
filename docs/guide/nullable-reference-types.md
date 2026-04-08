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

2. **Nullable property access**: Properties like `IMessage.CorrelationId`, `IMessage.ClrType`, and `IQueueEntry.CorrelationId` are now nullable.

::: info Queue Job Null Safety
`IQueueJob<T>.ProcessAsync` takes a **non-nullable** `IQueueEntry<T>` parameter. `QueueJobBase<T>.RunAsync()` already checks for `null` after dequeuing and returns early — `ProcessAsync` is never called with a `null` entry.
:::

## Known `null!` Patterns (Bandaids)

The following areas use `= null!` as a default value to suppress NRT warnings. These are functional but not ideal — the `required` keyword (C# 11+) would be more correct for properties that must always be set.

## `required` Keyword Usage

The following types use the C# 11 `required` keyword to enforce initialization at construction time. This replaces the previous `= null!` pattern and provides compile-time safety.

### Queue Event Args

All queue event args classes use `required` for properties that are always populated by the queue infrastructure:

- `EnqueuingEventArgs<T>`: `Queue`, `Data`, `Options`
- `EnqueuedEventArgs<T>`: `Queue`, `Entry`
- `DequeuedEventArgs<T>`: `Queue`, `Entry`
- `LockRenewedEventArgs<T>`: `Queue`, `Entry`
- `CompletedEventArgs<T>`: `Queue`, `Entry`
- `AbandonedEventArgs<T>`: `Queue`, `Entry`
- `QueueDeletedEventArgs<T>`: `Queue`

### DTOs and Models

| Class | Properties with `required` |
|-------|--------------------------|
| `FileSpec` | `Path` |
| `WorkItemData` | `WorkItemId`, `Type`, `Data` |
| `InvalidateCache` | `CacheId` |
| `ItemExpiredEventArgs` | `Client`, `Key` |
| `CacheLockReleased` | `Resource` |
| `NextPageResult` | `Files` |
| `MessageBusBase.Subscriber` | `Type`, `Action` |

::: warning
`required` on serialized types enforces the property is present during `System.Text.Json` deserialization — a missing property throws `JsonException`. This is intentional: `WorkItemData`, `InvalidateCache`, and `CacheLockReleased` are all serialized over queues or message bus, and their required properties should always be present in the payload.
:::

### Remaining `null!` Patterns

The following areas still use `= null!`:

| Class | Field/Property | Reason |
|-------|---------------|--------|
| `QueueBehaviorBase<T>` | `_queue` field | Set via `Attach()` before any other method is called. Cannot use `required` on a `protected` field set by an interface method. |

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
