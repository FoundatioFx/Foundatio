using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Storage;

/// <summary>
/// Provides a scoped file storage implementation that prefixes all file paths with the specified scope.
/// Can optionally dispose the underlying storage when this instance is disposed.
/// </summary>
public class ScopedFileStorage : IFileStorage, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider, IHaveResiliencePolicyProvider
{
    private readonly string _pathPrefix;
    private readonly bool _shouldDispose;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopedFileStorage"/> class with the specified file storage and scope.
    /// </summary>
    /// <param name="storage">The underlying file storage to use.</param>
    /// <param name="scope">The scope for file paths. When specified, all operations will be prefixed with this scope.</param>
    /// <param name="shouldDispose">Whether to dispose the underlying file storage when this instance is disposed.
    /// Defaults to false, meaning the underlying file storage will not be disposed when this instance is disposed.
    /// Set to true to have the underlying file storage automatically disposed when this instance is disposed, enabling use with 'using' statements.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="storage"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scope"/> contains a wildcard character.</exception>
    public ScopedFileStorage(IFileStorage storage, string scope, bool shouldDispose = false)
    {
        UnscopedStorage = storage ?? throw new ArgumentNullException(nameof(storage));
        Scope = !String.IsNullOrWhiteSpace(scope) ? scope.Trim().NormalizePath() : null;
        _pathPrefix = Scope != null ? String.Concat(Scope, "/") : String.Empty;
        _shouldDispose = shouldDispose;

        // NOTE: we can't really check reliably using Path.GetInvalidPathChars() because each storage implementation and platform could be different.
        if (Scope is not null && Scope.Contains("*"))
            throw new ArgumentException("Scope cannot contain a wildcard character", nameof(scope));
    }

    public IFileStorage UnscopedStorage { get; private set; }

    public string Scope { get; private set; }

    ISerializer IHaveSerializer.Serializer => UnscopedStorage.Serializer;
    ILogger IHaveLogger.Logger => UnscopedStorage.GetLogger();
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => UnscopedStorage.GetLoggerFactory();
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => UnscopedStorage.GetResiliencePolicyProvider();
    TimeProvider IHaveTimeProvider.TimeProvider => UnscopedStorage.GetTimeProvider();

    [Obsolete($"Use {nameof(GetFileStreamAsync)} with {nameof(StreamMode)} instead to define read or write behaviour of stream")]
    public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default)
        => GetFileStreamAsync(path, StreamMode.Read, cancellationToken);

    public Task<Stream> GetFileStreamAsync(string path, StreamMode streamMode, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        return UnscopedStorage.GetFileStreamAsync(String.Concat(_pathPrefix, path), streamMode, cancellationToken);
    }

    public async Task<FileSpec> GetFileInfoAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        var file = await UnscopedStorage.GetFileInfoAsync(String.Concat(_pathPrefix, path)).AnyContext();
        if (file != null)
            file.Path = file.Path.Substring(_pathPrefix.Length);

        return file;
    }

    public Task<bool> ExistsAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        return UnscopedStorage.ExistsAsync(String.Concat(_pathPrefix, path));
    }

    public Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        return UnscopedStorage.SaveFileAsync(String.Concat(_pathPrefix, path), stream, cancellationToken);
    }

    public Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(newPath))
            throw new ArgumentNullException(nameof(newPath));

        return UnscopedStorage.RenameFileAsync(String.Concat(_pathPrefix, path), String.Concat(_pathPrefix, newPath), cancellationToken);
    }

    public Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(targetPath))
            throw new ArgumentNullException(nameof(targetPath));

        return UnscopedStorage.CopyFileAsync(String.Concat(_pathPrefix, path), String.Concat(_pathPrefix, targetPath), cancellationToken);
    }

    public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        return UnscopedStorage.DeleteFileAsync(String.Concat(_pathPrefix, path), cancellationToken);
    }

    public Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default)
    {
        searchPattern = !String.IsNullOrEmpty(searchPattern) ? String.Concat(_pathPrefix, searchPattern) : String.Concat(_pathPrefix, "*");
        return UnscopedStorage.DeleteFilesAsync(searchPattern, cancellation);
    }

    public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default)
    {
        if (pageSize <= 0)
            return PagedFileListResult.Empty;

        searchPattern = !String.IsNullOrEmpty(searchPattern) ? String.Concat(_pathPrefix, searchPattern) : String.Concat(_pathPrefix, "*");
        var unscopedResult = await UnscopedStorage.GetPagedFileListAsync(pageSize, searchPattern, cancellationToken).AnyContext();

        foreach (var file in unscopedResult.Files)
            file.Path = file.Path.Substring(_pathPrefix.Length);

        return new PagedFileListResult(unscopedResult.Files, unscopedResult.HasMore, unscopedResult.HasMore ? _ => NextPage(unscopedResult) : null);
    }

    private async Task<NextPageResult> NextPage(PagedFileListResult result)
    {
        var success = await result.NextPageAsync().AnyContext();

        foreach (var file in result.Files)
            file.Path = file.Path.Substring(_pathPrefix.Length);

        return new NextPageResult
        {
            Success = success,
            HasMore = result.HasMore,
            Files = result.Files,
            NextPageFunc = _ => NextPage(result)
        };
    }

    public void Dispose()
    {
        if (_shouldDispose)
            UnscopedStorage.Dispose();
    }
}
