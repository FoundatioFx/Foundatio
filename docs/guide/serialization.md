# Serialization

All Foundatio implementations use serialization for storing and transmitting data. Understanding serialization options helps you optimize performance and choose the right format for your needs.

## ISerializer Interface

Foundatio defines a simple serialization interface that all implementations use:

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Serializer/ISerializer.cs)

```csharp
public interface ISerializer
{
    void Serialize(object value, Stream output);
    object Deserialize(Stream data, Type objectType);
}

// Text serializers (JSON, XML) also implement ITextSerializer
public interface ITextSerializer : ISerializer { }
```

This abstraction allows you to swap serializers without changing your code.

## Default Serializer

Foundatio uses `SystemTextJsonSerializer` (System.Text.Json) by default:

```csharp
// Default - uses System.Text.Json
var cache = new InMemoryCacheClient();

// Equivalent to:
var cache = new InMemoryCacheClient(o =>
    o.Serializer = new SystemTextJsonSerializer());
```

**Why System.Text.Json?**

- Built into .NET (no extra dependencies)
- Fast and efficient
- Human-readable JSON format
- Good balance of performance and debuggability

## Custom JSON Options

Configure System.Text.Json serialization behavior:

```csharp
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() }
};

var serializer = new SystemTextJsonSerializer(jsonOptions);

var cache = new InMemoryCacheClient(o => o.Serializer = serializer);
var queue = new InMemoryQueue<WorkItem>(o => o.Serializer = serializer);
var messageBus = new InMemoryMessageBus(o => o.Serializer = serializer);
```

## Global Default Serializer

Set the default serializer for all new instances that don't explicitly specify one:

```csharp
// Set globally (affects all new instances that don't specify a serializer)
DefaultSerializer.Instance = new SystemTextJsonSerializer(myJsonOptions);

// Now all new instances use your custom serializer
var cache = new InMemoryCacheClient(); // Uses your custom serializer
var queue = new InMemoryQueue<WorkItem>(); // Uses your custom serializer
```

**How it works:**

- When you don't specify a serializer, `SharedOptions.Serializer` falls back to `DefaultSerializer.Instance`
- This allows you to configure serialization once for your entire application
- Useful for setting up camelCase naming, custom converters, or other JSON options globally

## Available Serializers

Foundatio provides several serializer implementations via NuGet packages:

### System.Text.Json (Default)

```bash
# Included in Foundatio package (no extra install needed)
dotnet add package Foundatio
```

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Serializer/SystemTextJsonSerializer.cs)

```csharp
var serializer = new SystemTextJsonSerializer(jsonOptions);
```

**When to use:**

- Default choice for most applications
- Good performance and .NET native support
- Human-readable JSON for debugging
- Well-supported by .NET ecosystem

### Newtonsoft.Json (Json.NET)

```bash
dotnet add package Foundatio.JsonNet
```

```csharp
var settings = new JsonSerializerSettings
{
    TypeNameHandling = TypeNameHandling.Auto,
    ContractResolver = new CamelCasePropertyNamesContractResolver()
};
var serializer = new JsonNetSerializer(settings);
```

**When to use:**

- Need `$type` handling for polymorphic types
- Existing codebase uses Newtonsoft.Json extensively
- Require specific Newtonsoft.Json features not in System.Text.Json
- Legacy compatibility

### MessagePack (Binary)

```bash
dotnet add package Foundatio.MessagePack
```

```csharp
var options = MessagePackSerializerOptions.Standard
    .WithResolver(ContractlessStandardResolver.Instance);
var serializer = new MessagePackSerializer(options);
```

**When to use:**

- High-throughput scenarios where size and speed are critical
- Queue messages, cache values in high-volume systems
- Network bandwidth is limited
- Binary format is acceptable (not human-readable)

**Performance:** ~2-5x faster than JSON, 50-70% smaller payloads

## Performance Comparison

| Serializer | Speed | Size | Human Readable | Type Info | Dependencies |
|------------|-------|------|----------------|-----------|--------------|
| System.Text.Json | Fast | Medium | ✅ | ❌ | Built-in |
| MessagePack | **Very Fast** | **Small** | ❌ | Optional | MessagePack NuGet |
| Newtonsoft.Json | Medium | Medium | ✅ | ✅ | Newtonsoft.Json NuGet |

## Choosing the Right Serializer

### Use System.Text.Json (Default) When:

- Starting a new project
- You want good balance of speed, size, and debuggability
- You don't need advanced features like `$type` handling
- You prefer built-in .NET support

### Use MessagePack When:

- Processing high message volumes (>10k messages/sec)
- Network bandwidth or storage size is constrained
- Speed is more important than human-readability
- You have control over both producer and consumer

```csharp
// High-throughput queue example
var serializer = new MessagePackSerializer();
var queue = new RedisQueue<HighVolumeEvent>(o =>
{
    o.Serializer = serializer;
    o.ConnectionString = connectionString;
});
```

### Use Newtonsoft.Json When:

- You need `$type` handling for polymorphic serialization
- Migrating from legacy code that uses Json.NET
- Require specific Json.NET features (custom converters, complex scenarios)

```csharp
var settings = new JsonSerializerSettings
{
    TypeNameHandling = TypeNameHandling.Auto
};
var serializer = new JsonNetSerializer(settings);
var cache = new RedisCacheClient(o => o.Serializer = serializer);
```

## Using Serializers with Foundatio

All Foundatio implementations accept a serializer via their options:

```csharp
var serializer = new MessagePackSerializer();

// Caching
var cache = new InMemoryCacheClient(o => o.Serializer = serializer);

// Queues
var queue = new InMemoryQueue<WorkItem>(o => o.Serializer = serializer);

// Messaging
var messageBus = new InMemoryMessageBus(o => o.Serializer = serializer);

// Storage (for metadata serialization)
var storage = new InMemoryFileStorage(o => o.Serializer = serializer);
```

For DI configuration and shared options across implementations, see [Dependency Injection](/guide/dependency-injection).

## Serialization Considerations

### Binary vs Text Serializers

**Text Serializers (JSON):**

- Implement `ITextSerializer`
- Human-readable (can debug with text tools)
- Slightly larger payloads
- Compatible across different systems/languages

**Binary Serializers (MessagePack):**

- Implement `ISerializer` only
- Much faster and smaller
- Not human-readable
- Requires same serializer on both ends

### Shared Message Bus Topics

When using a shared message bus topic across multiple applications:

```csharp
// All apps must use the SAME serializer
var serializer = new SystemTextJsonSerializer(); // or MessagePackSerializer

services.AddSingleton<ISerializer>(serializer);
services.AddSingleton<IMessageBus>(sp =>
    new RedisMessageBus(o =>
    {
        o.Topic = "events"; // Shared topic
        o.Serializer = sp.GetRequiredService<ISerializer>();
    }));
```

::: warning
If different applications use different serializers on the same topic, deserialization will fail. Coordinate serializer choice across all consumers.
:::

## Next Steps

- [Caching](/guide/caching) - Cache-specific serialization patterns
- [Queues](/guide/queues) - Queue serialization and message size considerations
- [Messaging](/guide/messaging) - Message bus serialization and shared topics
- [Resilience](/guide/resilience) - Configure resilience policies
