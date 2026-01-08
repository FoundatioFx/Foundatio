# Foundatio.Aliyun

Foundatio provides Alibaba Cloud OSS file storage implementation. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio.Aliyun)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `AliyunFileStorage` | `IFileStorage` | Foundatio.Aliyun |

## Installation

```bash
dotnet add package Foundatio.Aliyun
```

## Usage

```csharp
using Foundatio.Storage;

var storage = new AliyunFileStorage(o =>
    o.ConnectionString = connectionString);

await storage.SaveFileAsync("documents/report.pdf", pdfStream);
var stream = await storage.GetFileStreamAsync("documents/report.pdf");
```

## Configuration

**Connection String** (required):

```csharp
var storage = new AliyunFileStorage(o =>
{
    o.ConnectionString = "endpoint=oss-cn-hangzhou.aliyuncs.com;accessKeyId=xxx;accessKeySecret=yyy;bucket=my-bucket";
});
```

For additional options including `LoggerFactory`, `Serializer`, `TimeProvider`, and `ResiliencePolicyProvider`, see the [AliyunFileStorageOptions source](https://github.com/FoundatioFx/Foundatio.Aliyun/blob/main/src/Foundatio.Aliyun/Storage/AliyunFileStorageOptions.cs).

## Next Steps

- [File Storage Guide](/guide/storage) - Usage patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
