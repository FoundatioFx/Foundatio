# Foundatio.Storage.SshNet

Foundatio provides SFTP-based file storage via SSH.NET. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio.Storage.SshNet)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `SshNetFileStorage` | `IFileStorage` | Foundatio.Storage.SshNet |

## Installation

```bash
dotnet add package Foundatio.Storage.SshNet
```

## Usage

```csharp
using Foundatio.Storage;

var storage = new SshNetFileStorage(o =>
    o.ConnectionString = "host=sftp.example.com;username=user;password=pass;path=/uploads");

await storage.SaveFileAsync("documents/report.pdf", pdfStream);
var stream = await storage.GetFileStreamAsync("documents/report.pdf");
```

## Configuration

| Option | Type | Required | Description |
|--------|------|----------|-------------|
| `ConnectionString` | `string` | ✅ | Connection string |
| `PrivateKey` | `Stream` | | Private key for authentication |
| `PrivateKeyPassPhrase` | `string` | | Private key passphrase |
| `Proxy` | `string` | | Proxy server |
| `ProxyType` | `ProxyTypes` | | Proxy type |

For additional options, see [SshNetFileStorageOptions source](https://github.com/FoundatioFx/Foundatio.Storage.SshNet/blob/main/src/Foundatio.Storage.SshNet/Storage/SshNetFileStorageOptions.cs).

## Next Steps

- [File Storage Guide](/guide/storage) - Usage patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
