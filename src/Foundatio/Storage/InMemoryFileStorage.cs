using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Storage;

public class InMemoryFileStorage : IFileStorage
{
    private readonly ConcurrentDictionary<string, (FileSpec Spec, byte[] Data)> _storage = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISerializer _serializer;
    protected readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public InMemoryFileStorage() : this(o => o) { }

    public InMemoryFileStorage(InMemoryFileStorageOptions options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        MaxFileSize = options.MaxFileSize;
        MaxFiles = options.MaxFiles;
        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        _timeProvider = options.TimeProvider;
        _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
    }

    public InMemoryFileStorage(Builder<InMemoryFileStorageOptionsBuilder, InMemoryFileStorageOptions> config)
        : this(config(new InMemoryFileStorageOptionsBuilder()).Build()) { }

    public long MaxFileSize { get; set; }
    public long MaxFiles { get; set; }
    ISerializer IHaveSerializer.Serializer => _serializer;

    [Obsolete($"Use {nameof(GetFileStreamAsync)} with {nameof(StreamMode)} instead to define read or write behaviour of stream")]
    public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default) =>
        GetFileStreamAsync(path, StreamMode.Read, cancellationToken);

    public Task<Stream> GetFileStreamAsync(string path, StreamMode streamMode, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Getting file stream for {Path}", normalizedPath);

        switch (streamMode)
        {
            case StreamMode.Read:
                if (_storage.TryGetValue(normalizedPath, out var file))
                    return Task.FromResult<Stream>(new MemoryStream(file.Data));

                _logger.LogError("Unable to get file stream for {Path}: File Not Found", normalizedPath);
                return Task.FromResult<Stream>(null);
            case StreamMode.Write:
                var stream = new MemoryStream();
                var actionableStream = new ActionableStream(stream, () =>
                {
                    stream.Position = 0;
                    byte[] contents = stream.ToArray();
                    if (contents.Length > MaxFileSize)
                        throw new ArgumentException($"File size {contents.Length.ToFileSizeDisplay()} exceeds the maximum size of {MaxFileSize.ToFileSizeDisplay()}.");

                    AddOrUpdate(normalizedPath, contents);
                });

                return Task.FromResult<Stream>(actionableStream);
            default:
                throw new ArgumentException("Invalid stream mode", nameof(streamMode));
        }
    }

    public async Task<FileSpec> GetFileInfoAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Getting file info for {Path}", normalizedPath);

        if (await ExistsAsync(normalizedPath).AnyContext())
            return _storage[normalizedPath].Spec.DeepClone();

        _logger.LogError("Unable to get file info for {Path}: File Not Found", normalizedPath);
        return null;
    }

    public Task<bool> ExistsAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Checking if {Path} exists", normalizedPath);

        return Task.FromResult(_storage.ContainsKey(normalizedPath));
    }

    private static byte[] ReadBytes(Stream input)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }

    public Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Saving {Path}", normalizedPath);

        var contents = ReadBytes(stream);
        if (contents.Length > MaxFileSize)
            throw new ArgumentException($"File size {contents.Length.ToFileSizeDisplay()} exceeds the maximum size of {MaxFileSize.ToFileSizeDisplay()}.");

        AddOrUpdate(normalizedPath, contents);

        return Task.FromResult(true);
    }

    private void AddOrUpdate(string path, byte[] contents)
    {
        string normalizedPath = path.NormalizePath();

        _storage.AddOrUpdate(normalizedPath, (new FileSpec
        {
            Created = _timeProvider.GetUtcNow().UtcDateTime,
            Modified = _timeProvider.GetUtcNow().UtcDateTime,
            Path = normalizedPath,
            Size = contents.Length
        }, contents), (_, file) => (new FileSpec
        {
            Created = file.Spec.Created,
            Modified = _timeProvider.GetUtcNow().UtcDateTime,
            Path = file.Spec.Path,
            Size = contents.Length
        }, contents));

        while (MaxFiles >= 0 && _storage.Count > MaxFiles)
            _storage.TryRemove(_storage.OrderByDescending(kvp => kvp.Value.Spec.Created).First().Key, out _);
    }

    public Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(newPath))
            throw new ArgumentNullException(nameof(newPath));

        string normalizedPath = path.NormalizePath();
        string normalizedNewPath = newPath.NormalizePath();
        _logger.LogInformation("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);

        if (!_storage.TryGetValue(normalizedPath, out var file))
        {
            _logger.LogDebug("Error renaming {Path} to {NewPath}: File not found", normalizedPath, normalizedNewPath);
            return Task.FromResult(false);
        }

        AddOrUpdate(normalizedNewPath, file.Data.ToArray());
        _storage.TryRemove(normalizedPath, out _);

        return Task.FromResult(true);
    }

    public Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(targetPath))
            throw new ArgumentNullException(nameof(targetPath));

        string normalizedPath = path.NormalizePath();
        string normalizedNewPath = targetPath.NormalizePath();
        _logger.LogInformation("Copying {Path} to {TargetPath}", normalizedPath, normalizedNewPath);


        if (!_storage.TryGetValue(normalizedPath, out var file))
        {
            _logger.LogDebug("Error copying {Path} to {NewPath}: File not found", normalizedPath, normalizedNewPath);
            return Task.FromResult(false);
        }

        AddOrUpdate(normalizedNewPath, file.Data.ToArray());

        return Task.FromResult(true);
    }

    public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Deleting {Path}", normalizedPath);

        if (_storage.TryRemove(normalizedPath, out _))
            return Task.FromResult(true);

        _logger.LogError("Unable to delete {Path}: File not found", normalizedPath);
        return Task.FromResult(false);
    }

    public Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default)
    {
        if (String.IsNullOrEmpty(searchPattern) || searchPattern == "*")
        {
            _storage.Clear();
            return Task.FromResult(0);
        }

        searchPattern = searchPattern.NormalizePath();
        int count = 0;

        if (searchPattern[searchPattern.Length - 1] == Path.DirectorySeparatorChar)
            searchPattern = $"{searchPattern}*";
        else if (!searchPattern.EndsWith(Path.DirectorySeparatorChar + "*") && !Path.HasExtension(searchPattern))
            searchPattern = Path.Combine(searchPattern, "*");

        var regex = new Regex($"^{Regex.Escape(searchPattern).Replace("\\*", ".*?")}$");

        var keys = _storage.Keys.Where(k => regex.IsMatch(k)).Select(k => _storage[k].Spec).ToList();

        _logger.LogInformation("Deleting {FileCount} files matching {SearchPattern} (Regex={SearchPatternRegex})", keys.Count, searchPattern, regex);
        foreach (var key in keys)
        {
            _logger.LogTrace("Deleting {Path}", key.Path);
            _storage.TryRemove(key.Path, out _);
            count++;
        }

        _logger.LogTrace("Finished deleting {FileCount} files matching {SearchPattern}", count, searchPattern);

        return Task.FromResult(count);
    }

    public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default)
    {
        if (pageSize <= 0)
            return PagedFileListResult.Empty;

        if (String.IsNullOrEmpty(searchPattern))
            searchPattern = "*";

        searchPattern = searchPattern.NormalizePath();

        var result = new PagedFileListResult(async s => await GetFilesAsync(searchPattern, 1, pageSize, cancellationToken));
        await result.NextPageAsync().AnyContext();
        return result;
    }

    private Task<NextPageResult> GetFilesAsync(string searchPattern, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var list = new List<FileSpec>();
        int pagingLimit = pageSize;
        int skip = (page - 1) * pagingLimit;
        if (pagingLimit < Int32.MaxValue)
            pagingLimit++;

        var regex = new Regex($"^{Regex.Escape(searchPattern).Replace("\\*", ".*?")}$");

        _logger.LogTrace(s => s.Property("Limit", pagingLimit).Property("Skip", skip), "Getting file list matching {SearchPattern}...", regex);
        list.AddRange(_storage.Keys.Where(k => regex.IsMatch(k)).Select(k => _storage[k].Spec.DeepClone()).Skip(skip).Take(pagingLimit).ToList());

        bool hasMore = false;
        if (list.Count == pagingLimit)
        {
            hasMore = true;
            list.RemoveAt(pagingLimit - 1);
        }

        return Task.FromResult(new NextPageResult
        {
            Success = true,
            HasMore = hasMore,
            Files = list,
            NextPageFunc = hasMore ? async _ => await GetFilesAsync(searchPattern, page + 1, pageSize, cancellationToken) : null
        });
    }

    public void Dispose()
    {
        _storage?.Clear();
    }
}
