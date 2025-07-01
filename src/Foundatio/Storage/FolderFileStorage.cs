using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Foundatio.Utility.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Storage;

public class FolderFileStorage : IFileStorage, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider, IHaveResiliencePolicyProvider
{
    private readonly AsyncLock _lock = new();
    private readonly ISerializer _serializer;
    protected readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IResiliencePolicyProvider _resiliencePolicyProvider;

    public FolderFileStorage(FolderFileStorageOptions options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        _loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger(GetType());
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _resiliencePolicyProvider = options.ResiliencePolicyProvider;

        string folder = PathHelper.ExpandPath(options.Folder);
        if (!Path.IsPathRooted(folder))
            folder = Path.GetFullPath(folder);

        char lastCharacter = folder[folder.Length - 1];
        if (!lastCharacter.Equals(Path.DirectorySeparatorChar) && !lastCharacter.Equals(Path.AltDirectorySeparatorChar))
            folder += Path.DirectorySeparatorChar;

        Folder = folder;

        _logger.LogInformation("Creating {Directory} directory", folder);
        Directory.CreateDirectory(folder);
    }

    public FolderFileStorage(Builder<FolderFileStorageOptionsBuilder, FolderFileStorageOptions> config)
        : this(config(new FolderFileStorageOptionsBuilder()).Build()) { }

    public string Folder { get; set; }
    ISerializer IHaveSerializer.Serializer => _serializer;
    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => _resiliencePolicyProvider;

    [Obsolete($"Use {nameof(GetFileStreamAsync)} with {nameof(StreamMode)} instead to define read or write behaviour of stream")]
    public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default)
        => GetFileStreamAsync(path, StreamMode.Read, cancellationToken);

    public Task<Stream> GetFileStreamAsync(string path, StreamMode streamMode, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        string fullPath = Path.Combine(Folder, normalizedPath);
        EnsureDirectory(fullPath);

        var stream = streamMode == StreamMode.Read ? File.OpenRead(fullPath) : new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        return Task.FromResult<Stream>(stream);
    }

    public Task<FileSpec> GetFileInfoAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Getting file stream for {Path}", normalizedPath);

        var info = new FileInfo(Path.Combine(Folder, normalizedPath));
        if (!info.Exists)
        {
            _logger.LogError("Unable to get file info for {Path}: File Not Found", normalizedPath);
            return Task.FromResult<FileSpec>(null);
        }

        return Task.FromResult(new FileSpec
        {
            Path = normalizedPath.Replace(Folder, String.Empty),
            Created = info.CreationTimeUtc,
            Modified = info.LastWriteTimeUtc,
            Size = info.Length,
            Data = { { "IsReadOnly", info.IsReadOnly } }
        });
    }

    public Task<bool> ExistsAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Checking if {Path} exists", normalizedPath);
        return Task.FromResult(File.Exists(Path.Combine(Folder, normalizedPath)));
    }

    public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        try
        {
            using var fileStream = await GetFileStreamAsync(path, StreamMode.Write, cancellationToken);
            await stream.CopyToAsync(fileStream).AnyContext();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving {Path}: {Message}", path, ex.Message);
            return false;
        }
    }

    private void EnsureDirectory(string normalizedPath)
    {
        string directory = Path.GetDirectoryName(normalizedPath);
        if (directory == null)
            return;

        _logger.LogTrace("Ensuring director: {Directory}", directory);
        Directory.CreateDirectory(directory);
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

        try
        {
            using (await _lock.LockAsync().AnyContext())
            {
                string directory = Path.GetDirectoryName(normalizedNewPath);
                if (directory != null)
                {
                    _logger.LogInformation("Creating {Directory} directory", directory);
                    Directory.CreateDirectory(Path.Combine(Folder, directory));
                }

                string oldFullPath = Path.Combine(Folder, normalizedPath);
                string newFullPath = Path.Combine(Folder, normalizedNewPath);
                try
                {
                    File.Move(oldFullPath, newFullPath);
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Error renaming {Path} to {NewPath}: Deleting {NewFullPath}", normalizedPath, normalizedNewPath, newFullPath);
                    File.Delete(newFullPath);

                    _logger.LogTrace("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);
                    File.Move(oldFullPath, newFullPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);
            return false;
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

        try
        {
            using (await _lock.LockAsync().AnyContext())
            {
                string directory = Path.GetDirectoryName(normalizedTargetPath);
                if (directory != null)
                {
                    _logger.LogInformation("Creating {Directory} directory", directory);
                    Directory.CreateDirectory(Path.Combine(Folder, directory));
                }

                File.Copy(Path.Combine(Folder, normalizedPath), Path.Combine(Folder, normalizedTargetPath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying {Path} to {TargetPath}: {Message}", normalizedPath, normalizedTargetPath, ex.Message);
            return false;
        }

        return true;
    }

    public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        string normalizedPath = path.NormalizePath();
        _logger.LogTrace("Deleting {Path}", normalizedPath);

        try
        {
            File.Delete(Path.Combine(Folder, normalizedPath));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogError(ex, "Unable to delete {Path}: {Message}", normalizedPath, ex.Message);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default)
    {
        int count = 0;

        if (String.IsNullOrEmpty(searchPattern) || searchPattern == "*")
        {
            if (Directory.Exists(Folder))
            {
                _logger.LogInformation("Deleting {Directory} directory", Folder);
                count += Directory.EnumerateFiles(Folder, "*.*", SearchOption.AllDirectories).Count();
                Directory.Delete(Folder, true);
                _logger.LogTrace("Finished deleting {Directory} directory with {FileCount} files", Folder, count);
            }

            return Task.FromResult(count);
        }

        searchPattern = searchPattern.NormalizePath();
        string path = Path.Combine(Folder, searchPattern);
        if (path[path.Length - 1] == Path.DirectorySeparatorChar || path.EndsWith($"{Path.DirectorySeparatorChar}*"))
        {
            string directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory))
            {
                _logger.LogInformation("Deleting {Directory} directory", directory);
                count += Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories).Count();
                Directory.Delete(directory, true);
                _logger.LogTrace("Finished deleting {Directory} directory with {FileCount} files", directory, count);
                return Task.FromResult(count);
            }

            return Task.FromResult(0);
        }

        if (Directory.Exists(path))
        {
            _logger.LogInformation("Deleting {Directory} directory", path);
            count += Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).Count();
            Directory.Delete(path, true);
            _logger.LogTrace("Finished deleting {Directory} directory with {FileCount} files", path, count);
            return Task.FromResult(count);
        }

        _logger.LogInformation("Deleting files matching {SearchPattern}", searchPattern);
        foreach (string file in Directory.EnumerateFiles(Folder, searchPattern, SearchOption.AllDirectories))
        {
            _logger.LogTrace("Deleting {Path}", file);
            File.Delete(file);
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

        if (!Directory.Exists(Path.GetDirectoryName(Path.Combine(Folder, searchPattern))))
        {
            _logger.LogTrace("Returning empty file list matching {SearchPattern}: Directory Not Found", searchPattern);
            return PagedFileListResult.Empty;
        }

        var result = new PagedFileListResult(s => Task.FromResult(GetFiles(searchPattern, 1, pageSize)));
        await result.NextPageAsync().AnyContext();
        return result;
    }

    private NextPageResult GetFiles(string searchPattern, int page, int pageSize)
    {
        var list = new List<FileSpec>();
        int pagingLimit = pageSize;
        int skip = (page - 1) * pagingLimit;
        if (pagingLimit < Int32.MaxValue)
            pagingLimit++;

        _logger.LogTrace(s => s.Property("Limit", pagingLimit).Property("Skip", skip), "Getting file list matching {SearchPattern}...", searchPattern);
        foreach (string path in Directory.EnumerateFiles(Folder, searchPattern, SearchOption.AllDirectories).Skip(skip).Take(pagingLimit))
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                continue;

            list.Add(new FileSpec
            {
                Path = info.FullName.Replace(Folder, String.Empty),
                Created = info.CreationTimeUtc,
                Modified = info.LastWriteTimeUtc,
                Size = info.Length,
                Data = { { "IsReadOnly", info.IsReadOnly } }
            });
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
            NextPageFunc = hasMore ? _ => Task.FromResult(GetFiles(searchPattern, page + 1, pageSize)) : null
        };
    }

    public void Dispose()
    {
    }
}
