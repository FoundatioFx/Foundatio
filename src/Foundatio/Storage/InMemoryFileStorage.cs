using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Storage;

public class InMemoryFileStorage : IFileStorage
{
    private readonly Dictionary<string, Tuple<FileSpec, byte[]>> _storage = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLock _lock = new();
    private readonly ISerializer _serializer;
    protected readonly ILogger _logger;

    public InMemoryFileStorage() : this(o => o) { }

    public InMemoryFileStorage(InMemoryFileStorageOptions options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        MaxFileSize = options.MaxFileSize;
        MaxFiles = options.MaxFiles;
        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
    }

    public InMemoryFileStorage(Builder<InMemoryFileStorageOptionsBuilder, InMemoryFileStorageOptions> config)
        : this(config(new InMemoryFileStorageOptionsBuilder()).Build()) { }

    public long MaxFileSize { get; set; }
    public long MaxFiles { get; set; }
    ISerializer IHaveSerializer.Serializer => _serializer;

    [Obsolete($"Use {nameof(GetFileStreamAsync)} with {nameof(FileAccess)} instead to define read or write behaviour of stream")]
    public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default) =>
        GetFileStreamAsync(path, StreamMode.Read, cancellationToken);

    public async Task<Stream> GetFileStreamAsync(string path, StreamMode streamMode, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Getting file stream for {Path}", normalizedPath);

        using (await _lock.LockAsync().AnyContext())
        {
            if (!_storage.ContainsKey(normalizedPath))
            {
                _logger.LogError("Unable to get file stream for {Path}: File Not Found", normalizedPath);
                return null;
            }

            return new MemoryStream(_storage[normalizedPath].Item2);
        }
    }

    public async Task<FileSpec> GetFileInfoAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Getting file info for {Path}", normalizedPath);

        if (await ExistsAsync(normalizedPath).AnyContext())
            return _storage[normalizedPath].Item1.DeepClone();

        _logger.LogError("Unable to get file info for {Path}: File Not Found", normalizedPath);
        return null;
    }

    public async Task<bool> ExistsAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Checking if {Path} exists", normalizedPath);

        using (await _lock.LockAsync().AnyContext())
        {
            return _storage.ContainsKey(normalizedPath);
        }
    }

    private static byte[] ReadBytes(Stream input)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }

    public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default)
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

        using (await _lock.LockAsync().AnyContext())
        {
            _storage[normalizedPath] = Tuple.Create(new FileSpec
            {
                Created = SystemClock.UtcNow,
                Modified = SystemClock.UtcNow,
                Path = normalizedPath,
                Size = contents.Length
            }, contents);

            if (_storage.Count > MaxFiles)
                _storage.Remove(_storage.OrderByDescending(kvp => kvp.Value.Item1.Created).First().Key);
        }

        return true;
    }

    public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(newPath))
            throw new ArgumentNullException(nameof(newPath));

        string normalizedPath = path.NormalizePath();
        string normalizedNewPath = newPath.NormalizePath();
        _logger.LogInformation("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);

        using (await _lock.LockAsync().AnyContext())
        {
            if (!_storage.ContainsKey(normalizedPath))
            {
                _logger.LogDebug("Error renaming {Path} to {NewPath}: File not found", normalizedPath, normalizedNewPath);
                return false;
            }

            _storage[normalizedNewPath] = _storage[normalizedPath];
            _storage[normalizedNewPath].Item1.Path = normalizedNewPath;
            _storage[normalizedNewPath].Item1.Modified = SystemClock.UtcNow;
            _storage.Remove(normalizedPath);
        }

        return true;
    }

    public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(targetPath))
            throw new ArgumentNullException(nameof(targetPath));

        string normalizedPath = path.NormalizePath();
        string normalizedTargetPath = targetPath.NormalizePath();
        _logger.LogInformation("Copying {Path} to {TargetPath}", normalizedPath, normalizedTargetPath);

        using (await _lock.LockAsync().AnyContext())
        {
            if (!_storage.ContainsKey(normalizedPath))
            {
                _logger.LogDebug("Error copying {Path} to {TargetPath}: File not found", normalizedPath, normalizedTargetPath);
                return false;
            }

            _storage[normalizedTargetPath] = _storage[normalizedPath];
            _storage[normalizedTargetPath].Item1.Path = normalizedTargetPath;
            _storage[normalizedTargetPath].Item1.Modified = SystemClock.UtcNow;
        }

        return true;
    }

    public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Deleting {Path}", normalizedPath);

        using (await _lock.LockAsync().AnyContext())
        {
            if (!_storage.ContainsKey(normalizedPath))
            {
                _logger.LogError("Unable to delete {Path}: File not found", normalizedPath);
                return false;
            }

            _storage.Remove(normalizedPath);
        }

        return true;
    }

    public async Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default)
    {
        if (String.IsNullOrEmpty(searchPattern) || searchPattern == "*")
        {
            using (await _lock.LockAsync().AnyContext())
            {
                _storage.Clear();
            }

            return 0;
        }

        searchPattern = searchPattern.NormalizePath();
        int count = 0;

        if (searchPattern[searchPattern.Length - 1] == Path.DirectorySeparatorChar)
            searchPattern = $"{searchPattern}*";
        else if (!searchPattern.EndsWith(Path.DirectorySeparatorChar + "*") && !Path.HasExtension(searchPattern))
            searchPattern = Path.Combine(searchPattern, "*");

        var regex = new Regex($"^{Regex.Escape(searchPattern).Replace("\\*", ".*?")}$");

        using (await _lock.LockAsync().AnyContext())
        {
            var keys = _storage.Keys.Where(k => regex.IsMatch(k)).Select(k => _storage[k].Item1).ToList();

            _logger.LogInformation("Deleting {FileCount} files matching {SearchPattern} (Regex={SearchPatternRegex})", keys.Count, searchPattern, regex);
            foreach (var key in keys)
            {
                _logger.LogTrace("Deleting {Path}", key.Path);
                _storage.Remove(key.Path);
                count++;
            }

            _logger.LogTrace("Finished deleting {FileCount} files matching {SearchPattern}", count, searchPattern);
        }

        return count;
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

    private async Task<NextPageResult> GetFilesAsync(string searchPattern, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var list = new List<FileSpec>();
        int pagingLimit = pageSize;
        int skip = (page - 1) * pagingLimit;
        if (pagingLimit < Int32.MaxValue)
            pagingLimit++;

        var regex = new Regex($"^{Regex.Escape(searchPattern).Replace("\\*", ".*?")}$");

        using (await _lock.LockAsync().AnyContext())
        {
            _logger.LogTrace(s => s.Property("Limit", pagingLimit).Property("Skip", skip), "Getting file list matching {SearchPattern}...", regex);
            list.AddRange(_storage.Keys.Where(k => regex.IsMatch(k)).Select(k => _storage[k].Item1.DeepClone()).Skip(skip).Take(pagingLimit).ToList());
        }

        bool hasMore = false;
        if (list.Count == pagingLimit)
        {
            hasMore = true;
            list.RemoveAt(pagingLimit - 1);
        }

        return new NextPageResult
        {
            Success = true,
            HasMore = hasMore,
            Files = list,
            NextPageFunc = hasMore ? async _ => await GetFilesAsync(searchPattern, page + 1, pageSize, cancellationToken) : null
        };
    }

    public void Dispose()
    {
        _storage?.Clear();
    }
}
