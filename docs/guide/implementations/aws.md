# AWS Implementation

Foundatio provides AWS implementations for file storage and queuing using Amazon S3 and Amazon SQS.

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `S3FileStorage` | `IFileStorage` | Foundatio.AWS |
| `SQSQueue<T>` | `IQueue<T>` | Foundatio.AWS |

## Installation

```bash
dotnet add package Foundatio.AWS
```

## Amazon S3 Storage

Store files in Amazon S3 with full support for buckets, prefixes, and metadata.

### Basic Usage

```csharp
using Foundatio.Storage;
using Amazon;

var storage = new S3FileStorage(options =>
{
    options.Bucket = "my-files";
    options.Region = RegionEndpoint.USEast1;
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
var storage = new S3FileStorage(options =>
{
    // Bucket name
    options.Bucket = "my-files";

    // AWS Region
    options.Region = RegionEndpoint.USEast1;

    // Explicit credentials (optional - uses default chain if not specified)
    options.AccessKey = "AKIAIOSFODNN7EXAMPLE";
    options.SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    // Service URL for localstack or custom endpoints
    options.ServiceUrl = "http://localhost:4566";

    // Logger
    options.LoggerFactory = loggerFactory;

    // Serializer for metadata
    options.Serializer = serializer;
});
```

### Using IAM Roles (Recommended)

```csharp
// When running on AWS (EC2, ECS, Lambda, etc.)
// credentials are automatically picked up from IAM role
var storage = new S3FileStorage(options =>
{
    options.Bucket = "my-files";
    options.Region = RegionEndpoint.USEast1;
    // No credentials specified - uses instance profile
});
```

### Using AWS Profiles

```csharp
using Amazon.Runtime.CredentialManagement;

var chain = new CredentialProfileStoreChain();
chain.TryGetAWSCredentials("my-profile", out var credentials);

var storage = new S3FileStorage(options =>
{
    options.Bucket = "my-files";
    options.Region = RegionEndpoint.USEast1;
    options.Credentials = credentials;
});
```

### File Operations

```csharp
// List files with prefix
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
Console.WriteLine($"Size: {info?.Size}, Modified: {info?.Modified}");

// Copy files
await storage.CopyFileAsync("source.txt", "backup/source.txt");

// Delete files
await storage.DeleteFileAsync("old-file.txt");

// Delete multiple files
await storage.DeleteFilesAsync("temp/"); // Delete by prefix
```

### Presigned URLs

```csharp
// Generate presigned URL for direct client access
var client = new AmazonS3Client();
var request = new GetPreSignedUrlRequest
{
    BucketName = "my-files",
    Key = "documents/report.pdf",
    Expires = DateTime.UtcNow.AddHours(1)
};
var url = client.GetPreSignedURL(request);
```

### DI Registration

```csharp
services.AddSingleton<IFileStorage>(sp =>
    new S3FileStorage(options =>
    {
        options.Bucket = configuration["AWS:S3:Bucket"];
        options.Region = RegionEndpoint.GetBySystemName(
            configuration["AWS:Region"] ?? "us-east-1");
        options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
```

## Amazon SQS Queue

Use Amazon Simple Queue Service for reliable message queuing.

### Basic Usage

```csharp
using Foundatio.Queues;
using Amazon;

var queue = new SQSQueue<WorkItem>(options =>
{
    options.Name = "work-items";
    options.Region = RegionEndpoint.USEast1;
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
var queue = new SQSQueue<WorkItem>(options =>
{
    // Queue name
    options.Name = "work-items";

    // AWS Region
    options.Region = RegionEndpoint.USEast1;

    // Optional credentials
    options.AccessKey = "...";
    options.SecretKey = "...";

    // Visibility timeout
    options.WorkItemTimeout = TimeSpan.FromMinutes(5);

    // Retry settings
    options.Retries = 3;
    options.RetryDelay = TimeSpan.FromSeconds(30);

    // Long polling (reduces costs)
    options.WaitTimeSeconds = 20;

    // Batch receive
    options.MaxNumberOfMessages = 10;

    // Auto-create queue
    options.AutoCreateQueue = true;

    options.LoggerFactory = loggerFactory;
});
```

### FIFO Queues

```csharp
// FIFO queues guarantee message order and exactly-once delivery
var queue = new SQSQueue<OrderItem>(options =>
{
    options.Name = "orders.fifo";  // Must end with .fifo
    options.Region = RegionEndpoint.USEast1;
    options.IsFifo = true;
});

// Enqueue with message group
await queue.EnqueueAsync(item, new QueueEntryOptions
{
    Properties = new Dictionary<string, string>
    {
        ["MessageGroupId"] = orderId,
        ["MessageDeduplicationId"] = Guid.NewGuid().ToString()
    }
});
```

### Dead Letter Queue

```csharp
// Configure dead letter queue in AWS Console or via CloudFormation
// Foundatio will respect the DLQ configuration

var queue = new SQSQueue<WorkItem>(options =>
{
    options.Name = "work-items";
    options.Retries = 3;  // After 3 failures, moves to DLQ
});
```

### Processing Patterns

```csharp
// Continuous processing
await queue.StartWorkingAsync(async (entry, token) =>
{
    await ProcessWorkItemAsync(entry.Value);
});

// Batch processing
var entries = new List<IQueueEntry<WorkItem>>();
while (true)
{
    var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(1));
    if (entry == null) break;
    entries.Add(entry);
}

// Process batch
foreach (var entry in entries)
{
    await ProcessAsync(entry.Value);
    await entry.CompleteAsync();
}
```

### DI Registration

```csharp
services.AddSingleton<IQueue<WorkItem>>(sp =>
    new SQSQueue<WorkItem>(options =>
    {
        options.Name = configuration["AWS:SQS:QueueName"];
        options.Region = RegionEndpoint.GetBySystemName(
            configuration["AWS:Region"] ?? "us-east-1");
        options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
```

## Complete AWS Setup

### Combined Services

```csharp
public static IServiceCollection AddFoundatioAWS(
    this IServiceCollection services,
    IConfiguration configuration)
{
    var region = RegionEndpoint.GetBySystemName(
        configuration["AWS:Region"] ?? "us-east-1");

    // S3 Storage
    services.AddSingleton<IFileStorage>(sp =>
        new S3FileStorage(options =>
        {
            options.Bucket = configuration["AWS:S3:Bucket"];
            options.Region = region;
            options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
        }));

    return services;
}

// Add SQS queue
public static IServiceCollection AddSQSQueue<T>(
    this IServiceCollection services,
    string name,
    IConfiguration configuration) where T : class
{
    var region = RegionEndpoint.GetBySystemName(
        configuration["AWS:Region"] ?? "us-east-1");

    services.AddSingleton<IQueue<T>>(sp =>
        new SQSQueue<T>(options =>
        {
            options.Name = name;
            options.Region = region;
            options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
        }));

    return services;
}
```

### Configuration

```json
{
  "AWS": {
    "Region": "us-east-1",
    "S3": {
      "Bucket": "my-app-files"
    },
    "SQS": {
      "QueueName": "work-items"
    }
  }
}
```

### Environment Variables

```bash
# Standard AWS environment variables
AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE
AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
AWS_REGION=us-east-1
AWS_PROFILE=my-profile

# Application-specific
AWS_S3_BUCKET=my-app-files
AWS_SQS_QUEUE_NAME=work-items
```

## Local Development with LocalStack

### Docker Compose

```yaml
version: '3.8'
services:
  localstack:
    image: localstack/localstack
    ports:
      - "4566:4566"
    environment:
      - SERVICES=s3,sqs
      - DEBUG=1
      - DATA_DIR=/tmp/localstack/data
    volumes:
      - "./localstack:/tmp/localstack"
```

### Configuration for LocalStack

```csharp
var storage = new S3FileStorage(options =>
{
    options.Bucket = "test-bucket";
    options.ServiceUrl = "http://localhost:4566";
    options.ForcePathStyle = true;  // Required for LocalStack
    options.AccessKey = "test";
    options.SecretKey = "test";
});

var queue = new SQSQueue<WorkItem>(options =>
{
    options.Name = "test-queue";
    options.ServiceUrl = "http://localhost:4566";
    options.AccessKey = "test";
    options.SecretKey = "test";
});
```

### Create Resources

```bash
# Create S3 bucket
aws --endpoint-url=http://localhost:4566 s3 mb s3://test-bucket

# Create SQS queue
aws --endpoint-url=http://localhost:4566 sqs create-queue --queue-name test-queue
```

## Production Considerations

### IAM Policies

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "s3:GetObject",
        "s3:PutObject",
        "s3:DeleteObject",
        "s3:ListBucket"
      ],
      "Resource": [
        "arn:aws:s3:::my-files",
        "arn:aws:s3:::my-files/*"
      ]
    },
    {
      "Effect": "Allow",
      "Action": [
        "sqs:SendMessage",
        "sqs:ReceiveMessage",
        "sqs:DeleteMessage",
        "sqs:GetQueueAttributes"
      ],
      "Resource": "arn:aws:sqs:us-east-1:*:work-items"
    }
  ]
}
```

### Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddS3(options =>
    {
        options.BucketName = "my-files";
        options.S3Config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.USEast1
        };
    }, name: "aws-s3")
    .AddSqs(options =>
    {
        options.QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456789/work-items";
        options.Config = new AmazonSQSConfig
        {
            RegionEndpoint = RegionEndpoint.USEast1
        };
    }, name: "aws-sqs");
```

### Cost Optimization

```csharp
// S3: Use appropriate storage classes
var putRequest = new PutObjectRequest
{
    BucketName = "my-files",
    Key = "archive/old-data.zip",
    InputStream = stream,
    StorageClass = S3StorageClass.IntelligentTiering
};

// SQS: Use long polling to reduce costs
var queue = new SQSQueue<WorkItem>(options =>
{
    options.WaitTimeSeconds = 20;  // Long poll for 20 seconds
});

// SQS: Batch operations
var queue = new SQSQueue<WorkItem>(options =>
{
    options.MaxNumberOfMessages = 10;  // Receive up to 10 at once
});
```

### Encryption

```csharp
// S3: Server-side encryption
var storage = new S3FileStorage(options =>
{
    options.Bucket = "my-files";
    options.ServerSideEncryption = ServerSideEncryptionMethod.AES256;
    // Or use KMS
    options.ServerSideEncryption = ServerSideEncryptionMethod.AWSKMS;
    options.ServerSideEncryptionKeyManagementServiceKeyId = "key-id";
});

// SQS: Enable encryption in AWS Console or CloudFormation
```

## Best Practices

### 1. Use IAM Roles

```csharp
// ✅ IAM Role (no credentials in code)
var storage = new S3FileStorage(options =>
{
    options.Bucket = "my-files";
    options.Region = RegionEndpoint.USEast1;
});

// ❌ Hardcoded credentials
options.AccessKey = "AKIA...";
options.SecretKey = "...";
```

### 2. Bucket Naming

```csharp
// ✅ Globally unique, lowercase
options.Bucket = "mycompany-app-files-prod";

// ❌ Invalid names
options.Bucket = "MyBucket";  // No uppercase
options.Bucket = "files";     // Too generic, may conflict
```

### 3. Handle Transient Failures

```csharp
// AWS SDK has built-in retry
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

### 4. Use Appropriate Timeouts

```csharp
// For large file operations
var config = new AmazonS3Config
{
    RegionEndpoint = RegionEndpoint.USEast1,
    Timeout = TimeSpan.FromMinutes(5),
    ReadWriteTimeout = TimeSpan.FromMinutes(5)
};
```

### 5. Enable Logging

```csharp
// AWS SDK logging
AWSConfigs.LoggingConfig.LogTo = LoggingOptions.Console;
AWSConfigs.LoggingConfig.LogResponses = ResponseLoggingOption.OnError;
```

## S3 vs SQS Features

### S3 Storage Features

- Object storage (files of any size)
- Versioning
- Lifecycle policies
- Cross-region replication
- Event notifications
- Static website hosting

### SQS Queue Features

- At-least-once delivery
- FIFO queues (exactly-once)
- Dead letter queues
- Long polling
- Message delay
- Batch operations

## Next Steps

- [Azure Implementation](./azure) - Azure Storage and Service Bus
- [Redis Implementation](./redis) - Distributed caching
- [In-Memory Implementation](./in-memory) - Local development
