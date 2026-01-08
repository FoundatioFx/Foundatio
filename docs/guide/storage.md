# File Storage

File storage provides abstracted file operations with multiple backend implementations. Foundatio's `IFileStorage` interface allows you to work with files consistently across local disk, cloud storage, and more.

## The IFileStorage Interface

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Storage/IFileStorage.cs)

```csharp
public interface IFileStorage : IHaveSerializer, IDisposable
{
    Task<Stream> GetFileStreamAsync(string path, StreamMode streamMode,
                                     CancellationToken cancellationToken = default);
    Task<FileSpec> GetFileInfoAsync(string path);
    Task<bool> ExistsAsync(string path);
    Task<bool> SaveFileAsync(string path, Stream stream,
                             CancellationToken cancellationToken = default);
    Task<bool> RenameFileAsync(string path, string newPath,
                               CancellationToken cancellationToken = default);
    Task<bool> CopyFileAsync(string path, string targetPath,
                             CancellationToken cancellationToken = default);
    Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default);
    Task<int> DeleteFilesAsync(string searchPattern = null,
                               CancellationToken cancellation = default);
    Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100,
                                                     string searchPattern = null,
                                                     CancellationToken cancellationToken = default);
}
```

## Implementations

### InMemoryFileStorage

An in-memory storage for development and testing:

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Storage/InMemoryFileStorage.cs)

```csharp
using Foundatio.Storage;

var storage = new InMemoryFileStorage();

// Save a file
await storage.SaveFileAsync("documents/report.pdf", pdfStream);

// Read a file
var stream = await storage.GetFileStreamAsync("documents/report.pdf", StreamMode.Read);
```

### FolderFileStorage

File storage backed by the local file system:

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Storage/FolderFileStorage.cs)

```csharp
using Foundatio.Storage;

var storage = new FolderFileStorage(o => o.Folder = "/data/files");

// Files are stored in /data/files/documents/report.pdf
await storage.SaveFileAsync("documents/report.pdf", pdfStream);
```

### ScopedFileStorage

Prefix all paths with a scope:

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Storage/ScopedFileStorage.cs)

```csharp
using Foundatio.Storage;

var baseStorage = new FolderFileStorage(o => o.Folder = "/data");
var tenantStorage = new ScopedFileStorage(baseStorage, "tenant-abc");

// Path becomes: tenant-abc/documents/report.pdf
await tenantStorage.SaveFileAsync("documents/report.pdf", pdfStream);
```

### AzureFileStorage

Azure Blob Storage (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.AzureStorage/blob/main/src/Foundatio.AzureStorage/Storage/AzureFileStorage.cs)

```csharp
// dotnet add package Foundatio.AzureStorage

using Foundatio.AzureStorage.Storage;

var storage = new AzureFileStorage(o => {
    o.ConnectionString = "DefaultEndpointsProtocol=https;...";
    o.ContainerName = "files";
});
```

### S3FileStorage

AWS S3 Storage (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.AWS/blob/main/src/Foundatio.AWS/Storage/S3FileStorage.cs)

```csharp
// dotnet add package Foundatio.AWS

using Foundatio.AWS.Storage;

var storage = new S3FileStorage(o => {
    o.Region = RegionEndpoint.USEast1;
    o.Bucket = "my-files";
});
```

### RedisFileStorage

Redis-backed storage (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.Redis/blob/main/src/Foundatio.Redis/Storage/RedisFileStorage.cs)

```csharp
// dotnet add package Foundatio.Redis

using Foundatio.Redis.Storage;

var storage = new RedisFileStorage(o => {
    o.ConnectionMultiplexer = redis;
});
```

### MinioFileStorage

Minio object storage (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.Minio/blob/main/src/Foundatio.Minio/Storage/MinioFileStorage.cs)

```csharp
// dotnet add package Foundatio.Minio

using Foundatio.Minio.Storage;

var storage = new MinioFileStorage(o => {
    o.Endpoint = "localhost:9000";
    o.AccessKey = "minioadmin";
    o.SecretKey = "minioadmin";
    o.Bucket = "files";
});
```

### SshNetFileStorage

SFTP-backed storage (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.Storage.SshNet/blob/main/src/Foundatio.Storage.SshNet/SshNetFileStorage.cs)

```csharp
// dotnet add package Foundatio.Storage.SshNet

using Foundatio.Storage.SshNet;

var storage = new SshNetFileStorage(o => {
    o.Host = "sftp.example.com";
    o.Username = "user";
    o.Password = "password";
    o.WorkingDirectory = "/uploads";
});
```

## Basic Operations

### Saving Files

```csharp
var storage = new InMemoryFileStorage();

// Save from stream
using var stream = File.OpenRead("local-file.pdf");
await storage.SaveFileAsync("remote/file.pdf", stream);

// Save string content (extension method)
await storage.SaveFileAsync("config.json", """{"key": "value"}""");

// Save with object serialization (extension method)
await storage.SaveObjectAsync("data/user.json", new User { Name = "John" });
```

### Reading Files

```csharp
// Get file stream for reading
using var stream = await storage.GetFileStreamAsync("file.pdf", StreamMode.Read);

// Read as string (extension method)
string content = await storage.GetFileContentsAsync("config.json");

// Read and deserialize (extension method)
var user = await storage.GetObjectAsync<User>("data/user.json");

// Get raw bytes (extension method)
byte[] bytes = await storage.GetFileBytesAsync("image.png");
```

### File Information

```csharp
// Check if file exists
bool exists = await storage.ExistsAsync("file.pdf");

// Get file info
var fileSpec = await storage.GetFileInfoAsync("file.pdf");
if (fileSpec != null)
{
    Console.WriteLine($"Path: {fileSpec.Path}");
    Console.WriteLine($"Size: {fileSpec.Size} bytes");
    Console.WriteLine($"Modified: {fileSpec.Modified}");
    Console.WriteLine($"Created: {fileSpec.Created}");
}
```

### Modifying Files

```csharp
// Rename/move file
await storage.RenameFileAsync("old/path.pdf", "new/path.pdf");

// Copy file
await storage.CopyFileAsync("source.pdf", "backup/source.pdf");

// Delete file
await storage.DeleteFileAsync("file.pdf");

// Delete multiple files by pattern
int deleted = await storage.DeleteFilesAsync("temp/*");
```

### Listing Files

```csharp
// List all files
var files = await storage.GetFileListAsync();
foreach (var file in files)
{
    Console.WriteLine($"{file.Path} - {file.Size} bytes");
}

// List with pattern
var pdfFiles = await storage.GetFileListAsync("documents/*.pdf");

// Paged listing for large directories
var result = await storage.GetPagedFileListAsync(pageSize: 100, "logs/*");
do
{
    foreach (var file in result.Files)
    {
        Console.WriteLine(file.Path);
    }
} while (await result.NextPageAsync());
```

## Stream Modes

Control how file streams are opened:

```csharp
// Read mode - for reading existing files
using var readStream = await storage.GetFileStreamAsync("file.pdf", StreamMode.Read);

// Write mode - for creating/overwriting files
using var writeStream = await storage.GetFileStreamAsync("file.pdf", StreamMode.Write);
await someData.CopyToAsync(writeStream);
```

## Common Patterns

### File Upload/Download

```csharp
public class FileService
{
    private readonly IFileStorage _storage;

    public async Task<string> UploadAsync(IFormFile file, string folder)
    {
        var path = $"{folder}/{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";

        using var stream = file.OpenReadStream();
        await _storage.SaveFileAsync(path, stream);

        return path;
    }

    public async Task<Stream> DownloadAsync(string path)
    {
        if (!await _storage.ExistsAsync(path))
            throw new FileNotFoundException(path);

        return await _storage.GetFileStreamAsync(path, StreamMode.Read);
    }
}
```

### Organized File Structure

```csharp
public class DocumentStorage
{
    private readonly IFileStorage _storage;

    public string GetPath(int tenantId, int documentId, string fileName)
    {
        // Organized path: tenants/{id}/documents/{year}/{month}/{id}/{filename}
        var now = DateTime.UtcNow;
        return $"tenants/{tenantId}/documents/{now:yyyy}/{now:MM}/{documentId}/{fileName}";
    }

    public async Task SaveDocumentAsync(int tenantId, int documentId,
                                        string fileName, Stream content)
    {
        var path = GetPath(tenantId, documentId, fileName);
        await _storage.SaveFileAsync(path, content);
    }
}
```

### File Versioning

```csharp
public class VersionedFileStorage
{
    private readonly IFileStorage _storage;

    public async Task SaveVersionAsync(string basePath, Stream content)
    {
        var version = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var versionPath = $"{basePath}.v{version}";

        // Save new version
        await _storage.SaveFileAsync(versionPath, content);

        // Update "current" pointer
        await _storage.CopyFileAsync(versionPath, basePath);
    }

    public async Task<IEnumerable<FileSpec>> GetVersionsAsync(string basePath)
    {
        return await _storage.GetFileListAsync($"{basePath}.v*");
    }
}
```

### Temporary File Cleanup

```csharp
public class TempFileCleanup
{
    private readonly IFileStorage _storage;

    public async Task CleanupOldFilesAsync(TimeSpan maxAge)
    {
        var files = await _storage.GetFileListAsync("temp/*");
        var cutoff = DateTime.UtcNow - maxAge;

        foreach (var file in files)
        {
            if (file.Modified < cutoff)
            {
                await _storage.DeleteFileAsync(file.Path);
            }
        }
    }
}
```

### Multi-Tenant Storage

```csharp
public class TenantStorageFactory
{
    private readonly IFileStorage _baseStorage;

    public IFileStorage GetStorageForTenant(string tenantId)
    {
        return new ScopedFileStorage(_baseStorage, $"tenants/{tenantId}");
    }
}

// Usage
var tenantStorage = _storageFactory.GetStorageForTenant("tenant-123");
await tenantStorage.SaveFileAsync("documents/report.pdf", stream);
// Actual path: tenants/tenant-123/documents/report.pdf
```

## Extension Methods

Foundatio provides helpful extension methods:

```csharp
// String content
await storage.SaveFileAsync("text.txt", "Hello World");
string text = await storage.GetFileContentsAsync("text.txt");

// Bytes
await storage.SaveFileAsync("data.bin", byteArray);
byte[] bytes = await storage.GetFileBytesAsync("data.bin");

// Objects (serialized)
await storage.SaveObjectAsync("user.json", user);
var user = await storage.GetObjectAsync<User>("user.json");

// Non-paged file listing
var allFiles = await storage.GetFileListAsync();
var filtered = await storage.GetFileListAsync("*.pdf");
```

## Dependency Injection

### Basic Registration

```csharp
// In-memory (development)
services.AddSingleton<IFileStorage, InMemoryFileStorage>();

// Folder (local development with persistence)
services.AddSingleton<IFileStorage>(sp =>
    new FolderFileStorage(o => o.Folder = "./storage")
);

// Azure Blob (production)
services.AddSingleton<IFileStorage>(sp =>
    new AzureFileStorage(o => {
        o.ConnectionString = configuration["Azure:StorageConnectionString"];
        o.ContainerName = "files";
    })
);
```

### With Scoping

```csharp
services.AddSingleton<IFileStorage>(sp =>
    new FolderFileStorage(o => o.Folder = "/data")
);

// Scoped storage per tenant
services.AddScoped<IFileStorage>((sp, tenantId) =>
{
    var baseStorage = sp.GetRequiredService<IFileStorage>();
    return new ScopedFileStorage(baseStorage, $"tenant:{tenantId}");
});
```

## Best Practices

### 1. Use Meaningful Paths

```csharp
// ✅ Good: Organized, meaningful paths
"documents/invoices/2024/01/invoice-12345.pdf"
"users/user-123/avatars/profile.jpg"
"temp/uploads/session-abc/file.tmp"

// ❌ Bad: Flat, unclear paths
"file1.pdf"
"12345.pdf"
"abc123"
```

### 2. Include Extension in Path

```csharp
// ✅ Good: Extension present
await storage.SaveFileAsync("report.pdf", stream);

// ❌ Bad: No extension
await storage.SaveFileAsync("report", stream);
```

### 3. Use Scoped Storage for Isolation

```csharp
// Each tenant has isolated storage
var tenantStorage = new ScopedFileStorage(baseStorage, tenantId);
```

### 4. Handle Missing Files

```csharp
if (!await storage.ExistsAsync(path))
{
    throw new FileNotFoundException($"File not found: {path}");
}

var stream = await storage.GetFileStreamAsync(path, StreamMode.Read);
```

### 5. Dispose Streams Properly

```csharp
// ✅ Good: Using statement
using var stream = await storage.GetFileStreamAsync(path, StreamMode.Read);
await stream.CopyToAsync(destination);

// ❌ Bad: Not disposing
var stream = await storage.GetFileStreamAsync(path, StreamMode.Read);
await stream.CopyToAsync(destination);
// stream never disposed!
```

### 6. Use Appropriate Storage for Use Case

| Use Case | Recommended Storage |
|----------|---------------------|
| Development/Testing | InMemoryFileStorage |
| Local persistence | FolderFileStorage |
| Cloud applications | AzureFileStorage, S3FileStorage |
| Multi-cloud | MinioFileStorage |
| Legacy systems | SshNetFileStorage |

## FileSpec Properties

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Storage/FileSpec.cs)

```csharp
public class FileSpec
{
    public string Path { get; set; }
    public long Size { get; set; }
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
}
```

## Next Steps

- [Caching](./caching) - Cache file metadata
- [Jobs](./jobs) - Background file processing
- [Azure Implementation](./implementations/azure) - Production Azure setup
- [AWS Implementation](./implementations/aws) - Production AWS setup
