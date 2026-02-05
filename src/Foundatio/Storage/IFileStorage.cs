using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Storage;

/// <summary>
/// Provides a unified abstraction for file storage operations.
/// </summary>
public interface IFileStorage : IHaveSerializer, IDisposable
{
    [Obsolete($"Use {nameof(GetFileStreamAsync)} with {nameof(StreamMode)} instead to define read or write behaviour of stream")]
    Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a stream for reading from or writing to a file.
    /// </summary>
    /// <param name="path">The path to the file. Use forward slashes for directory separators.</param>
    /// <param name="streamMode">Whether to open the stream for reading or writing.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A stream for the file, or null if the file does not exist (read mode only).</returns>
    Task<Stream> GetFileStreamAsync(string path, StreamMode streamMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metadata about a file without retrieving its contents.
    /// </summary>
    /// <param name="path">The path to the file.</param>
    /// <returns>File metadata, or null if the file does not exist.</returns>
    Task<FileSpec> GetFileInfoAsync(string path);

    /// <summary>
    /// Checks whether a file exists at the specified path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if the file exists; otherwise, false.</returns>
    Task<bool> ExistsAsync(string path);

    /// <summary>
    /// Saves a file from a stream, creating or overwriting as needed.
    /// </summary>
    /// <param name="path">The destination path. Directories are created automatically.</param>
    /// <param name="stream">The content to save. The stream is read from its current position.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if the file was saved successfully.</returns>
    Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames or moves a file to a new path.
    /// </summary>
    /// <param name="path">The current file path.</param>
    /// <param name="newPath">The new file path.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if the file was renamed successfully.</returns>
    Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a file to a new path.
    /// </summary>
    /// <param name="path">The source file path.</param>
    /// <param name="targetPath">The destination file path.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if the file was copied successfully.</returns>
    Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file at the specified path.
    /// </summary>
    /// <param name="path">The path to the file to delete.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if the file was deleted; false if it did not exist.</returns>
    Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes multiple files matching a search pattern.
    /// </summary>
    /// <param name="searchPattern">
    /// A pattern to match file paths. Supports wildcards (* and ?).
    /// If null, deletes all files.
    /// </param>
    /// <param name="cancellation">Token to cancel the operation.</param>
    /// <returns>The number of files deleted.</returns>
    Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default);

    /// <summary>
    /// Lists files with pagination support for large directories.
    /// </summary>
    /// <param name="pageSize">Maximum number of files to return per page.</param>
    /// <param name="searchPattern">
    /// A pattern to match file paths. Supports wildcards (* and ?).
    /// If null, returns all files.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A paginated result that can be iterated to retrieve additional pages.</returns>
    Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Internal interface for pagination support.
/// </summary>
public interface IHasNextPageFunc
{
    Func<PagedFileListResult, Task<NextPageResult>> NextPageFunc { get; set; }
}

/// <summary>
/// Result of fetching the next page of files.
/// </summary>
public class NextPageResult
{
    /// <summary>
    /// Whether the page was fetched successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Whether more pages are available after this one.
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// The files in this page.
    /// </summary>
    public IReadOnlyCollection<FileSpec> Files { get; set; }

    /// <summary>
    /// Function to fetch the next page, or null if no more pages.
    /// </summary>
    public Func<PagedFileListResult, Task<NextPageResult>> NextPageFunc { get; set; }
}

/// <summary>
/// A paginated list of files that supports iterating through additional pages.
/// </summary>
public class PagedFileListResult : IHasNextPageFunc
{
    private static readonly IReadOnlyCollection<FileSpec> _empty = new ReadOnlyCollection<FileSpec>(Array.Empty<FileSpec>());

    /// <summary>
    /// An empty result with no files.
    /// </summary>
    public static readonly PagedFileListResult Empty = new(_empty);

    public PagedFileListResult(IReadOnlyCollection<FileSpec> files)
    {
        Files = files;
        HasMore = false;
        ((IHasNextPageFunc)this).NextPageFunc = null;
    }

    public PagedFileListResult(IReadOnlyCollection<FileSpec> files, bool hasMore, Func<PagedFileListResult, Task<NextPageResult>> nextPageFunc)
    {
        Files = files;
        HasMore = hasMore;
        ((IHasNextPageFunc)this).NextPageFunc = nextPageFunc;
    }

    public PagedFileListResult(Func<PagedFileListResult, Task<NextPageResult>> nextPageFunc)
    {
        ((IHasNextPageFunc)this).NextPageFunc = nextPageFunc;
    }

    /// <summary>
    /// The files in the current page.
    /// </summary>
    public IReadOnlyCollection<FileSpec> Files { get; private set; }

    /// <summary>
    /// Whether more pages are available.
    /// </summary>
    public bool HasMore { get; private set; }

    protected IDictionary<string, object> Data { get; } = new DataDictionary();
    Func<PagedFileListResult, Task<NextPageResult>> IHasNextPageFunc.NextPageFunc { get; set; }

    /// <summary>
    /// Fetches the next page of files, updating <see cref="Files"/> and <see cref="HasMore"/>.
    /// </summary>
    /// <returns>True if the next page was fetched successfully; false if no more pages or an error occurred.</returns>
    public async Task<bool> NextPageAsync()
    {
        if (((IHasNextPageFunc)this).NextPageFunc == null)
            return false;

        var result = await ((IHasNextPageFunc)this).NextPageFunc(this).AnyContext();
        if (result.Success)
        {
            Files = result.Files;
            HasMore = result.HasMore;
            ((IHasNextPageFunc)this).NextPageFunc = result.NextPageFunc;
        }
        else
        {
            Files = _empty;
            HasMore = false;
            ((IHasNextPageFunc)this).NextPageFunc = null;
        }

        return result.Success;
    }
}

/// <summary>
/// Metadata about a file in storage.
/// </summary>
[DebuggerDisplay("Path = {Path}, Created = {Created}, Modified = {Modified}, Size = {Size} bytes")]
public class FileSpec : IHaveData
{
    /// <summary>
    /// The full path to the file.
    /// </summary>
    public string Path { get; set; }

    /// <summary>
    /// When the file was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the file was last modified.
    /// </summary>
    public DateTime Modified { get; set; }

    /// <summary>
    /// The file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Additional provider-specific metadata (e.g., ETag, version ID, content type).
    /// </summary>
    public IDictionary<string, object> Data { get; } = new DataDictionary();
}

public static class FileStorageExtensions
{
    public static Task<bool> SaveObjectAsync<T>(this IFileStorage storage, string path, T data, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        var bytes = storage.Serializer.SerializeToBytes(data);
        return storage.SaveFileAsync(path, new MemoryStream(bytes), cancellationToken);
    }

    public static async Task<T> GetObjectAsync<T>(this IFileStorage storage, string path, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        using var stream = await storage.GetFileStreamAsync(path, StreamMode.Read, cancellationToken).AnyContext();
        if (stream != null)
            return storage.Serializer.Deserialize<T>(stream);

        return default;
    }

    public static async Task DeleteFilesAsync(this IFileStorage storage, IEnumerable<FileSpec> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        foreach (var file in files)
            await storage.DeleteFileAsync(file.Path).AnyContext();
    }

    public static async Task<string> GetFileContentsAsync(this IFileStorage storage, string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        using var stream = await storage.GetFileStreamAsync(path, StreamMode.Read).AnyContext();
        if (stream != null)
            return await new StreamReader(stream).ReadToEndAsync().AnyContext();

        return null;
    }

    public static async Task<byte[]> GetFileContentsRawAsync(this IFileStorage storage, string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        using var stream = await storage.GetFileStreamAsync(path, StreamMode.Read).AnyContext();
        if (stream == null)
            return null;

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).AnyContext()) > 0)
        {
            await ms.WriteAsync(buffer, 0, read).AnyContext();
        }

        return ms.ToArray();
    }

    public static Task<bool> SaveFileAsync(this IFileStorage storage, string path, string contents)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        return storage.SaveFileAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(contents ?? String.Empty)));
    }

    public static async Task<IReadOnlyCollection<FileSpec>> GetFileListAsync(this IFileStorage storage, string searchPattern = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var files = new List<FileSpec>();
        limit ??= Int32.MaxValue;
        var result = await storage.GetPagedFileListAsync(limit.Value, searchPattern, cancellationToken).AnyContext();
        do
        {
            files.AddRange(result.Files);
        } while (result.HasMore && files.Count < limit.Value && await result.NextPageAsync().AnyContext());

        return files;
    }
}
