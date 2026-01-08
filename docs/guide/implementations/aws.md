# Foundatio.AWS

Foundatio provides AWS implementations for file storage and queuing using Amazon S3 and Amazon SQS. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio.AWS)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `S3FileStorage` | `IFileStorage` | Foundatio.AWS |
| `SQSQueue<T>` | `IQueue<T>` | Foundatio.AWS |

## Installation

```bash
dotnet add package Foundatio.AWS
```

## S3FileStorage

Store files in Amazon S3 with full support for buckets, prefixes, and metadata.

```csharp
using Foundatio.Storage;

var storage = new S3FileStorage(o =>
{
    o.ConnectionString = connectionString;
    // Or: o.Bucket = "my-files"; o.Region = RegionEndpoint.USEast1;
});

await storage.SaveFileAsync("documents/report.pdf", pdfStream);
```

### Configuration

| Option | Type | Required | Description |
|--------|------|----------|-------------|
| `Bucket` | `string` | ✅ | S3 bucket name |
| `Region` | `RegionEndpoint` | ✅ | AWS region |
| `ConnectionString` | `string` | | Parses all settings |
| `Credentials` | `AWSCredentials` | | AWS credentials |
| `ServiceUrl` | `string` | | Custom endpoint (LocalStack) |

For additional options, see [S3FileStorageOptions source](https://github.com/FoundatioFx/Foundatio.AWS/blob/main/src/Foundatio.AWS/Storage/S3FileStorageOptions.cs).

## SQSQueue

AWS SQS queue implementation.

```csharp
using Foundatio.Queues;

var queue = new SQSQueue<WorkItem>(o =>
{
    o.ConnectionString = connectionString;
    o.Name = "work-items";
});

await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
var entry = await queue.DequeueAsync();
```

### Configuration

| Option | Type | Required | Default | Description |
|--------|------|----------|---------|-------------|
| `Name` | `string` | ✅ | | Queue name |
| `ConnectionString` | `string` | | | Connection string |
| `Region` | `RegionEndpoint` | | | AWS region |
| `CanCreateQueue` | `bool` | | `true` | Auto-create queue |
| `SupportDeadLetter` | `bool` | | `true` | Enable DLQ support |
| `ReadQueueTimeout` | `TimeSpan` | | 20s | Long polling timeout |

For additional options, see [SQSQueueOptions source](https://github.com/FoundatioFx/Foundatio.AWS/blob/main/src/Foundatio.AWS/Queues/SQSQueueOptions.cs).

## Next Steps

- [File Storage Guide](/guide/storage) - Usage patterns
- [Queues Guide](/guide/queues) - Queue processing patterns
- [Serialization](/guide/serialization) - Configure serialization
