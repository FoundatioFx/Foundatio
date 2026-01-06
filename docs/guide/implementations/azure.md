# Azure Implementation

Foundatio provides Azure implementations for storage and messaging using Azure Blob Storage and Azure Service Bus.

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `AzureFileStorage` | `IFileStorage` | Foundatio.AzureStorage |
| `AzureStorageQueue<T>` | `IQueue<T>` | Foundatio.AzureStorage |
| `AzureServiceBusQueue<T>` | `IQueue<T>` | Foundatio.AzureServiceBus |
| `AzureServiceBusMessageBus` | `IMessageBus` | Foundatio.AzureServiceBus |

## Installation

```bash
# Azure Storage (Blobs and Queues)
dotnet add package Foundatio.AzureStorage

# Azure Service Bus (Queues and Messaging)
dotnet add package Foundatio.AzureServiceBus
```

## Azure Blob Storage

Store files in Azure Blob Storage with full support for containers, virtual directories, and metadata.

### Basic Usage

```csharp
using Foundatio.Storage;

var storage = new AzureFileStorage(options =>
{
    options.ConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=...";
    options.ContainerName = "files";
});

// Save a file
await storage.SaveFileAsync("documents/report.pdf", pdfStream);

// Read a file
var stream = await storage.GetFileStreamAsync("documents/report.pdf");

// Get file contents as string
var content = await storage.GetFileContentsAsync("config/settings.json");
```

### Configuration Options

```csharp
var storage = new AzureFileStorage(options =>
{
    // Connection string
    options.ConnectionString = connectionString;

    // Container name
    options.ContainerName = "myfiles";

    // Create container if not exists
    options.ShouldCreateContainer = true;

    // Logger
    options.LoggerFactory = loggerFactory;

    // Serializer for metadata
    options.Serializer = serializer;
});
```

### Using Managed Identity

```csharp
using Azure.Identity;

var storage = new AzureFileStorage(options =>
{
    options.BlobServiceClient = new BlobServiceClient(
        new Uri("https://mystorageaccount.blob.core.windows.net"),
        new DefaultAzureCredential()
    );
    options.ContainerName = "files";
});
```

### File Operations

```csharp
// List files
var files = await storage.GetFileListAsync("documents/");
await foreach (var file in files)
{
    Console.WriteLine($"{file.Path} - {file.Size} bytes");
}

// Check existence
if (await storage.ExistsAsync("documents/report.pdf"))
{
    // File exists
}

// Get file info
var info = await storage.GetFileInfoAsync("documents/report.pdf");
Console.WriteLine($"Modified: {info?.Modified}");

// Copy files
await storage.CopyFileAsync("source.txt", "backup/source.txt");

// Delete files
await storage.DeleteFileAsync("old-file.txt");
await storage.DeleteFilesAsync("temp/"); // Delete folder
```

### DI Registration

```csharp
services.AddSingleton<IFileStorage>(sp =>
    new AzureFileStorage(options =>
    {
        options.ConnectionString =
            configuration.GetConnectionString("AzureStorage");
        options.ContainerName = "files";
        options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
```

## Azure Storage Queue

Use Azure Storage Queues for simple, reliable message queuing.

### Basic Usage

```csharp
using Foundatio.Queues;

var queue = new AzureStorageQueue<WorkItem>(options =>
{
    options.ConnectionString = connectionString;
    options.Name = "work-items";
});

// Enqueue
await queue.EnqueueAsync(new WorkItem { Id = 1, Data = "Process this" });

// Dequeue
var entry = await queue.DequeueAsync();
if (entry != null)
{
    await ProcessAsync(entry.Value);
    await entry.CompleteAsync();
}
```

### Configuration Options

```csharp
var queue = new AzureStorageQueue<WorkItem>(options =>
{
    options.ConnectionString = connectionString;
    options.Name = "work-items";

    // Visibility timeout
    options.WorkItemTimeout = TimeSpan.FromMinutes(5);

    // Retry settings
    options.Retries = 3;
    options.RetryDelay = TimeSpan.FromSeconds(30);

    // Dequeue batch size
    options.DequeueCount = 1;

    options.LoggerFactory = loggerFactory;
});
```

### DI Registration

```csharp
services.AddSingleton<IQueue<WorkItem>>(sp =>
    new AzureStorageQueue<WorkItem>(options =>
    {
        options.ConnectionString =
            configuration.GetConnectionString("AzureStorage");
        options.Name = "work-items";
        options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
```

## Azure Service Bus Queue

Use Azure Service Bus for enterprise-grade messaging with advanced features.

### Basic Usage

```csharp
using Foundatio.Queues;

var queue = new AzureServiceBusQueue<WorkItem>(options =>
{
    options.ConnectionString = serviceBusConnectionString;
    options.Name = "work-items";
});

// Enqueue
await queue.EnqueueAsync(new WorkItem { Id = 1 });

// Process messages
await queue.StartWorkingAsync(async (entry, token) =>
{
    await ProcessWorkItemAsync(entry.Value);
});
```

### Configuration Options

```csharp
var queue = new AzureServiceBusQueue<WorkItem>(options =>
{
    options.ConnectionString = connectionString;
    options.Name = "work-items";

    // Processing options
    options.WorkItemTimeout = TimeSpan.FromMinutes(5);
    options.Retries = 3;
    options.AutoCreateQueue = true;

    // Prefetch for better performance
    options.PrefetchCount = 10;

    options.LoggerFactory = loggerFactory;
});
```

### Advanced Features

```csharp
// Scheduled messages
await queue.EnqueueAsync(new WorkItem { Id = 1 }, new QueueEntryOptions
{
    DeliveryDelay = TimeSpan.FromHours(1)
});

// Sessions (ordered processing)
var queue = new AzureServiceBusQueue<OrderItem>(options =>
{
    options.ConnectionString = connectionString;
    options.Name = "orders";
    options.RequiresSession = true;
});

// Enqueue with session
await queue.EnqueueAsync(item, new QueueEntryOptions
{
    Properties = new Dictionary<string, string>
    {
        ["SessionId"] = orderId
    }
});
```

### DI Registration

```csharp
services.AddSingleton<IQueue<WorkItem>>(sp =>
    new AzureServiceBusQueue<WorkItem>(options =>
    {
        options.ConnectionString =
            configuration.GetConnectionString("ServiceBus");
        options.Name = "work-items";
        options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
```

## Azure Service Bus Message Bus

Use Azure Service Bus Topics for pub/sub messaging.

### Basic Usage

```csharp
using Foundatio.Messaging;

var messageBus = new AzureServiceBusMessageBus(options =>
{
    options.ConnectionString = connectionString;
    options.Topic = "events";
});

// Subscribe
await messageBus.SubscribeAsync<OrderCreatedEvent>(async message =>
{
    await HandleOrderCreatedAsync(message);
});

// Publish
await messageBus.PublishAsync(new OrderCreatedEvent { OrderId = "123" });
```

### Configuration Options

```csharp
var messageBus = new AzureServiceBusMessageBus(options =>
{
    options.ConnectionString = connectionString;
    options.Topic = "events";

    // Subscription name (unique per consumer)
    options.SubscriptionName = "order-processor";

    // Auto-create topic/subscription
    options.AutoCreateTopic = true;

    // Message prefetch
    options.PrefetchCount = 10;

    options.LoggerFactory = loggerFactory;
});
```

### Multiple Subscribers

```csharp
// Each service gets its own subscription
// All subscribers receive all messages

// Order Service
var orderBus = new AzureServiceBusMessageBus(options =>
{
    options.Topic = "events";
    options.SubscriptionName = "order-service";
});

// Notification Service
var notificationBus = new AzureServiceBusMessageBus(options =>
{
    options.Topic = "events";
    options.SubscriptionName = "notification-service";
});

// Both receive OrderCreatedEvent
await messageBus.PublishAsync(new OrderCreatedEvent());
```

### DI Registration

```csharp
services.AddSingleton<IMessageBus>(sp =>
    new AzureServiceBusMessageBus(options =>
    {
        options.ConnectionString =
            configuration.GetConnectionString("ServiceBus");
        options.Topic = "events";
        options.SubscriptionName = configuration["ServiceBus:SubscriptionName"];
        options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));

services.AddSingleton<IMessagePublisher>(sp =>
    sp.GetRequiredService<IMessageBus>());
services.AddSingleton<IMessageSubscriber>(sp =>
    sp.GetRequiredService<IMessageBus>());
```

## Complete Azure Setup

### Combined Services

```csharp
public static IServiceCollection AddFoundatioAzure(
    this IServiceCollection services,
    IConfiguration configuration)
{
    var storageConnection = configuration.GetConnectionString("AzureStorage");
    var serviceBusConnection = configuration.GetConnectionString("ServiceBus");

    // File Storage
    services.AddSingleton<IFileStorage>(sp =>
        new AzureFileStorage(options =>
        {
            options.ConnectionString = storageConnection;
            options.ContainerName = "files";
            options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
        }));

    // Message Bus
    services.AddSingleton<IMessageBus>(sp =>
        new AzureServiceBusMessageBus(options =>
        {
            options.ConnectionString = serviceBusConnection;
            options.Topic = "events";
            options.SubscriptionName =
                configuration["Azure:ServiceBus:SubscriptionName"];
            options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
        }));

    services.AddSingleton<IMessagePublisher>(sp =>
        sp.GetRequiredService<IMessageBus>());
    services.AddSingleton<IMessageSubscriber>(sp =>
        sp.GetRequiredService<IMessageBus>());

    return services;
}

// Add Service Bus queue
public static IServiceCollection AddAzureServiceBusQueue<T>(
    this IServiceCollection services,
    string name,
    IConfiguration configuration) where T : class
{
    services.AddSingleton<IQueue<T>>(sp =>
        new AzureServiceBusQueue<T>(options =>
        {
            options.ConnectionString =
                configuration.GetConnectionString("ServiceBus");
            options.Name = name;
            options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
        }));

    return services;
}
```

### Configuration

```json
{
  "ConnectionStrings": {
    "AzureStorage": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net",
    "ServiceBus": "Endpoint=sb://....servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=..."
  },
  "Azure": {
    "ServiceBus": {
      "SubscriptionName": "my-service"
    }
  }
}
```

## Managed Identity

### Azure Storage with Managed Identity

```csharp
using Azure.Identity;

var storage = new AzureFileStorage(options =>
{
    options.BlobServiceClient = new BlobServiceClient(
        new Uri("https://mystorageaccount.blob.core.windows.net"),
        new DefaultAzureCredential()
    );
    options.ContainerName = "files";
});
```

### Azure Service Bus with Managed Identity

```csharp
using Azure.Identity;

var messageBus = new AzureServiceBusMessageBus(options =>
{
    options.ServiceBusClient = new ServiceBusClient(
        "mynamespace.servicebus.windows.net",
        new DefaultAzureCredential()
    );
    options.Topic = "events";
});
```

## Production Considerations

### Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddAzureBlobStorage(storageConnection, name: "azure-storage")
    .AddAzureServiceBusQueue(serviceBusConnection, "work-items",
        name: "azure-servicebus-queue")
    .AddAzureServiceBusTopic(serviceBusConnection, "events",
        name: "azure-servicebus-topic");
```

### Retry Policies

```csharp
// Azure SDK has built-in retry policies
// Foundatio integrates with them automatically

var storage = new AzureFileStorage(options =>
{
    options.BlobServiceClient = new BlobServiceClient(
        connectionString,
        new BlobClientOptions
        {
            Retry =
            {
                MaxRetries = 5,
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(30),
                Mode = RetryMode.Exponential
            }
        });
});
```

### Cost Optimization

```csharp
// Use cool or archive tier for infrequently accessed files
var blobClient = containerClient.GetBlobClient("archive/old-data.zip");
await blobClient.SetAccessTierAsync(AccessTier.Cool);

// Batch operations where possible
var files = new[] { "file1.txt", "file2.txt", "file3.txt" };
foreach (var file in files)
{
    await storage.SaveFileAsync($"batch/{file}", content);
}
```

## Azure Storage vs Service Bus

### When to Use Azure Storage Queue

- Simple message queuing
- High volume, low latency not critical
- Cost-sensitive scenarios
- No advanced features needed

### When to Use Azure Service Bus

- Enterprise messaging requirements
- Ordered message processing (sessions)
- Scheduled messages
- Dead letter handling
- Topics and subscriptions (pub/sub)
- Transactions

## Best Practices

### 1. Use Managed Identity

```csharp
// ✅ Managed Identity (no secrets in code)
new DefaultAzureCredential()

// ❌ Connection strings with secrets
"AccountKey=..."
```

### 2. Container Naming

```csharp
// ✅ Lowercase, descriptive names
options.ContainerName = "user-uploads";

// ❌ Invalid characters
options.ContainerName = "User_Uploads"; // Invalid
```

### 3. Handle Transient Failures

```csharp
// Azure SDK handles retries automatically
// Add application-level resilience for business logic
var policy = new ResiliencePolicy
{
    MaxAttempts = 3,
    GetDelay = ResiliencePolicy.ExponentialDelay(TimeSpan.FromSeconds(1))
};

await policy.ExecuteAsync(async ct =>
{
    await storage.SaveFileAsync("file.txt", content);
}, cancellationToken);
```

### 4. Monitor and Alert

```csharp
// Use Application Insights
builder.Services.AddApplicationInsightsTelemetry();

// Log operations
logger.LogInformation("Saved file {Path} to Azure Storage", path);
```

## Next Steps

- [AWS Implementation](./aws) - S3 and SQS integration
- [Redis Implementation](./redis) - Distributed caching
- [In-Memory Implementation](./in-memory) - Local development
