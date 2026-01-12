using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Storage;

public class AzureFileStorage : IFileStorage, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider
{
    private readonly BlobContainerClient _container;
    private readonly ISerializer _serializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public AzureFileStorage(AzureFileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        _loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger(GetType());

        var clientOptions = new BlobClientOptions();
        options.ConfigureRetry?.Invoke(clientOptions.Retry);

        _container = new BlobContainerClient(options.ConnectionString, options.ContainerName, clientOptions);

        _logger.LogTrace("Checking if {Container} container exists", _container.Name);
        var response = _container.CreateIfNotExists();
        if (response != null)
            _logger.LogDebug("Created {Container}", _container.Name);
        else
            _logger.LogDebug("{Container} already exists", _container.Name);
    }

    public AzureFileStorage(Builder<AzureFileStorageOptionsBuilder, AzureFileStorageOptions> config)
        : this(config(new AzureFileStorageOptionsBuilder()).Build())
    {
    }

    ISerializer IHaveSerializer.Serializer => _serializer;
    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    public BlobContainerClient Container => _container;

    [Obsolete($"Use {nameof(GetFileStreamAsync)} with {nameof(StreamMode)} instead to define read or write behavior of stream")]
    public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default)
        => GetFileStreamAsync(path, StreamMode.Read, cancellationToken);

    public async Task<Stream> GetFileStreamAsync(string path, StreamMode streamMode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Getting file stream for {Path}", normalizedPath);

        var blobClient = _container.GetBlobClient(normalizedPath);

        try
        {
            return streamMode switch
            {
                StreamMode.Read => await blobClient.OpenReadAsync(cancellationToken: cancellationToken).AnyContext(),
                StreamMode.Write => await blobClient.OpenWriteAsync(overwrite: true, cancellationToken: cancellationToken).AnyContext(),
                _ => throw new NotSupportedException($"Stream mode {streamMode} is not supported.")
            };
        }
        catch (RequestFailedException ex) when (ex.Status is 404)
        {
            _logger.LogDebug(ex, "[{Status}] Unable to get file stream for {Path}: File Not Found", ex.Status, normalizedPath);
            return null;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "[{Status}] Unable to get file stream for {Path}: {Message}", ex.Status, normalizedPath, ex.Message);
            throw new StorageException("Unable to get file stream.", ex);
        }
    }

    public async Task<FileSpec> GetFileInfoAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Getting file info for {Path}", normalizedPath);

        var blobClient = _container.GetBlobClient(normalizedPath);
        try
        {
            var properties = await blobClient.GetPropertiesAsync().AnyContext();
            return ToFileInfo(normalizedPath, properties.Value);
        }
        catch (RequestFailedException ex) when (ex.Status is 404)
        {
            _logger.LogDebug(ex, "[{Status}] Unable to get file info for {Path}: File Not Found", ex.Status, normalizedPath);
            return null;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "[{Status}] Unable to get file info for {Path}: {Message}", ex.Status, normalizedPath, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to get file info for {Path}: {Message}", normalizedPath, ex.Message);
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Checking if {Path} exists", normalizedPath);

        var blobClient = _container.GetBlobClient(normalizedPath);
        var response = await blobClient.ExistsAsync().AnyContext();
        return response.Value;
    }

    public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(stream);

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Saving {Path}", normalizedPath);

        try
        {
            var blobClient = _container.GetBlobClient(normalizedPath);
            await blobClient.UploadAsync(stream, overwrite: true, cancellationToken: cancellationToken).AnyContext();
            return true;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "[{Status}] Error saving {Path}: {Message}", ex.Status, normalizedPath, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving {Path}: {Message}", normalizedPath, ex.Message);
            return false;
        }
    }

    public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(newPath);

        string normalizedPath = NormalizePath(path);
        string normalizedNewPath = NormalizePath(newPath);
        _logger.LogDebug("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);

        try
        {
            if (!await CopyFileAsync(normalizedPath, normalizedNewPath, cancellationToken).AnyContext())
            {
                _logger.LogError("Unable to rename {Path} to {NewPath}", normalizedPath, normalizedNewPath);
                return false;
            }

            var oldBlob = _container.GetBlobClient(normalizedPath);
            _logger.LogDebug("Deleting renamed {Path}", normalizedPath);
            var deleteResponse = await oldBlob.DeleteIfExistsAsync(cancellationToken: cancellationToken).AnyContext();
            if (!deleteResponse.Value)
            {
                _logger.LogDebug("Unable to delete renamed {Path}", normalizedPath);
                return false;
            }

            return true;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "[{Status}] Unable to rename {Path} to {NewPath}: {Message}", ex.Status, normalizedPath, normalizedNewPath, ex.Message);
            return false;
        }
    }

    public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        string normalizedPath = NormalizePath(path);
        string normalizedTargetPath = NormalizePath(targetPath);
        _logger.LogDebug("Copying {Path} to {TargetPath}", normalizedPath, normalizedTargetPath);

        try
        {
            var sourceBlob = _container.GetBlobClient(normalizedPath);
            var targetBlob = _container.GetBlobClient(normalizedTargetPath);

            var copyOperation = await targetBlob.StartCopyFromUriAsync(sourceBlob.Uri, cancellationToken: cancellationToken).AnyContext();

            // Wait for copy operation to complete
            await copyOperation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
            if (!copyOperation.HasCompleted)
            {
                _logger.LogError("Copy operation did not complete for {Path} to {TargetPath}", normalizedPath, normalizedTargetPath);
                return false;
            }

            // Check final status
            var properties = await targetBlob.GetPropertiesAsync(cancellationToken: cancellationToken).AnyContext();
            if (properties.Value.CopyStatus == CopyStatus.Success)
                return true;

            _logger.LogError("Copy operation failed for {Path} to {TargetPath}: {CopyStatus}", normalizedPath, normalizedTargetPath, properties.Value.CopyStatus);
            return false;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "[{Status}] Unable to copy {Path} to {TargetPath}: {Message}", ex.Status, normalizedPath, normalizedTargetPath, ex.Message);
            return false;
        }
    }

    public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Deleting {Path}", normalizedPath);

        try
        {
            var blobClient = _container.GetBlobClient(normalizedPath);
            var response = await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken).AnyContext();
            if (!response.Value)
                _logger.LogDebug("Unable to delete {Path}: File not found", normalizedPath);
            return response.Value;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "[{Status}] Unable to delete {Path}: {Message}", ex.Status, normalizedPath, ex.Message);
            return false;
        }
    }

    public async Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = default)
    {
        var files = await GetFileListAsync(searchPattern, cancellationToken: cancellationToken).AnyContext();
        int count = 0;

        // TODO: We could batch this, but we should ensure the batch isn't thousands of files.
        _logger.LogDebug("Deleting {FileCount} files matching {SearchPattern}", files.Count, searchPattern);

        foreach (var file in files)
        {
            await DeleteFileAsync(file.Path, cancellationToken).AnyContext();
            count++;
        }
        _logger.LogTrace("Finished deleting {FileCount} files matching {SearchPattern}", count, searchPattern);

        return count;
    }

    public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default)
    {
        if (pageSize <= 0)
            return PagedFileListResult.Empty;

        var result = new PagedFileListResult(_ => GetFiles(searchPattern, 1, pageSize, cancellationToken));
        await result.NextPageAsync().AnyContext();
        return result;
    }

    private async Task<NextPageResult> GetFiles(string searchPattern, int page, int pageSize, CancellationToken cancellationToken)
    {
        int pagingLimit = pageSize;
        int skip = (page - 1) * pagingLimit;
        if (pagingLimit < Int32.MaxValue)
            pagingLimit++;

        var list = await GetFileListAsync(searchPattern, pagingLimit, skip, cancellationToken).AnyContext();
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
            NextPageFunc = hasMore ? _ => GetFiles(searchPattern, page + 1, pageSize, cancellationToken) : null
        };
    }

    private async Task<List<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0)
            return new List<FileSpec>();

        var criteria = GetRequestCriteria(searchPattern);

        int totalLimit = limit.GetValueOrDefault(Int32.MaxValue) < Int32.MaxValue
            ? skip.GetValueOrDefault() + limit.GetValueOrDefault()
            : Int32.MaxValue;

        _logger.LogTrace("Getting file list: Prefix={Prefix} Pattern={Pattern} Limit={Limit}", criteria.Prefix, criteria.Pattern, totalLimit);

        var blobs = new List<FileSpec>();
        await foreach (var blobItem in _container.GetBlobsAsync(new GetBlobsOptions { Prefix = criteria.Prefix }, cancellationToken: cancellationToken))
        {
            // TODO: Verify if it's possible to create empty folders in storage. If so, don't return them.
            if (criteria.Pattern != null && !criteria.Pattern.IsMatch(blobItem.Name))
            {
                _logger.LogTrace("Skipping {Path}: Doesn't match pattern", blobItem.Name);
                continue;
            }

            blobs.Add(ToFileInfo(blobItem));
            if (blobs.Count >= totalLimit)
                break;
        }

        if (skip.HasValue)
            blobs = blobs.Skip(skip.Value).ToList();

        if (limit.HasValue)
            blobs = blobs.Take(limit.Value).ToList();

        return blobs;
    }

    private static FileSpec ToFileInfo(BlobItem blob)
    {
        return new FileSpec
        {
            Path = blob.Name,
            Size = blob.Properties.ContentLength ?? -1,
            Created = blob.Properties.CreatedOn?.UtcDateTime ?? DateTime.MinValue,
            Modified = blob.Properties.LastModified?.UtcDateTime ?? DateTime.MinValue
        };
    }

    private static FileSpec ToFileInfo(string path, BlobProperties properties)
    {
        return new FileSpec
        {
            Path = path,
            Size = properties.ContentLength,
            Created = properties.CreatedOn.UtcDateTime,
            Modified = properties.LastModified.UtcDateTime
        };
    }

    private static string NormalizePath(string path)
    {
        return path?.Replace('\\', '/');
    }

    private record SearchCriteria
    {
        public string Prefix { get; set; }
        public Regex Pattern { get; set; }
    }

    private SearchCriteria GetRequestCriteria(string searchPattern)
    {
        if (String.IsNullOrEmpty(searchPattern))
            return new SearchCriteria { Prefix = String.Empty };

        string normalizedSearchPattern = NormalizePath(searchPattern);
        int wildcardPos = normalizedSearchPattern.IndexOf('*');
        bool hasWildcard = wildcardPos >= 0;

        string prefix = normalizedSearchPattern;
        Regex patternRegex = null;

        if (hasWildcard)
        {
            patternRegex = new Regex($"^{Regex.Escape(normalizedSearchPattern).Replace("\\*", ".*?")}$");
            int slashPos = normalizedSearchPattern.LastIndexOf('/');
            prefix = slashPos >= 0 ? normalizedSearchPattern[..slashPos] : String.Empty;
        }

        return new SearchCriteria
        {
            Prefix = prefix,
            Pattern = patternRegex
        };
    }

    public void Dispose()
    {
    }
}
