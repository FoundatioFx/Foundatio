# Foundatio.Minio

Foundatio provides Minio file storage for S3-compatible object storage. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio.Minio)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `MinioFileStorage` | `IFileStorage` | Foundatio.Minio |

## Installation

```bash
dotnet add package Foundatio.Minio
```

## Usage

```csharp
using Foundatio.Storage;

var storage = new MinioFileStorage(o =>
    o.ConnectionString = "endpoint=play.min.io;accessKey=minioadmin;secretKey=minioadmin;bucket=my-bucket");

await storage.SaveFileAsync("documents/report.pdf", pdfStream);
var stream = await storage.GetFileStreamAsync("documents/report.pdf");
```

## Configuration

| Option | Type | Required | Default | Description |
|--------|------|----------|---------|-------------|
| `ConnectionString` | `string` | ✅ | | Connection string |
| `AutoCreateBucket` | `bool` | | `false` | Auto-create bucket if missing |

For additional options, see [MinioFileStorageOptions source](https://github.com/FoundatioFx/Foundatio.Minio/blob/main/src/Foundatio.Minio/Storage/MinioFileStorageOptions.cs).

## Next Steps

- [File Storage Guide](/guide/storage) - Usage patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
