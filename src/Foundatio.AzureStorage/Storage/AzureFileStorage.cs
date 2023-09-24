using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Azure.Extensions;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Storage {
    public class AzureFileStorage : IFileStorage {
        private readonly CloudBlobContainer _container;
        private readonly ISerializer _serializer;
        protected readonly ILogger _logger;

        public AzureFileStorage(AzureFileStorageOptions options) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;

            var account = CloudStorageAccount.Parse(options.ConnectionString);
            var client = account.CreateCloudBlobClient();
            
            _container = client.GetContainerReference(options.ContainerName);
            
            _logger.LogTrace("Checking if {Container} container exists", _container.Name);
            bool created = _container.CreateIfNotExistsAsync().GetAwaiter().GetResult();
            if (created)
                _logger.LogInformation("Created {Container}", _container.Name);
        }

        public AzureFileStorage(Builder<AzureFileStorageOptionsBuilder, AzureFileStorageOptions> config)
            : this(config(new AzureFileStorageOptionsBuilder()).Build()) { }

        ISerializer IHaveSerializer.Serializer => _serializer;

        public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default)
            => GetFileStreamAsync(path, StreamMode.Read, cancellationToken);

        public async Task<Stream> GetFileStreamAsync(string path, StreamMode streamMode, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path)); 
            
            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Getting file stream for {Path}", normalizedPath);

            var blockBlob = _container.GetBlockBlobReference(normalizedPath);

            try {
                return streamMode switch {
                    StreamMode.Read => await blockBlob.OpenReadAsync(null, null, null, cancellationToken).AnyContext(),
                    StreamMode.Write => await blockBlob.OpenWriteAsync(null, null, null, cancellationToken).AnyContext(),
                    _ => null
                };
            } catch (StorageException ex) when (ex is { RequestInformation.HttpStatusCode: 404}) {
                _logger.LogDebug(ex, "Unable to get file stream for {Path}: File Not Found", normalizedPath);
                return null;
            }
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Getting file info for {Path}", normalizedPath);
            
            var blob = _container.GetBlockBlobReference(normalizedPath);
            try {
                await blob.FetchAttributesAsync().AnyContext();
                return blob.ToFileInfo();
            } catch (Exception ex) {
                _logger.LogError(ex, "Unable to get file info for {Path}: {Message}", normalizedPath, ex.Message);
            }

            return null;
        }

        public Task<bool> ExistsAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Checking if {Path} exists", normalizedPath);

            var blockBlob = _container.GetBlockBlobReference(normalizedPath);
            return blockBlob.ExistsAsync();
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            
            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Saving {Path}", normalizedPath);
            
            var blockBlob = _container.GetBlockBlobReference(normalizedPath);
            await blockBlob.UploadFromStreamAsync(stream, null, null, null, cancellationToken).AnyContext();

            return true;
        }

        public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            string normalizedPath = NormalizePath(path);
            string normalizedNewPath = NormalizePath(newPath);
            _logger.LogInformation("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);
            
            var oldBlob = _container.GetBlockBlobReference(normalizedPath);
            if (!(await CopyFileAsync(normalizedPath, normalizedNewPath, cancellationToken).AnyContext())) { 
                _logger.LogError("Unable to rename {Path} to {NewPath}", normalizedPath, normalizedNewPath);
                return false;
            }

            _logger.LogDebug("Deleting renamed {Path}", normalizedPath);
            bool deleted = await oldBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, null, null, null, cancellationToken).AnyContext();
            if (!deleted) {
                _logger.LogDebug("Unable to delete renamed {Path}", normalizedPath);
                return false;
            }

            return true;
        }

        public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            string normalizedPath = NormalizePath(path);
            string normalizedTargetPath = NormalizePath(targetPath);
            _logger.LogInformation("Copying {Path} to {TargetPath}", normalizedPath, normalizedTargetPath);
            
            var oldBlob = _container.GetBlockBlobReference(normalizedPath);
            var newBlob = _container.GetBlockBlobReference(normalizedTargetPath);

            await newBlob.StartCopyAsync(oldBlob, cancellationToken).AnyContext();
            while (newBlob.CopyState.Status == CopyStatus.Pending)
                await SystemClock.SleepAsync(50, cancellationToken).AnyContext();

            return newBlob.CopyState.Status == CopyStatus.Success;
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Deleting {Path}", normalizedPath);
            
            var blockBlob = _container.GetBlockBlobReference(normalizedPath);
            return blockBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, null, null, null, cancellationToken);
        }

        public async Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = default) {
            var files = await GetFileListAsync(searchPattern, cancellationToken: cancellationToken).AnyContext();
            int count = 0;

            // TODO: We could batch this, but we should ensure the batch isn't thousands of files.
            _logger.LogInformation("Deleting {FileCount} files matching {SearchPattern}", files.Count, searchPattern);
            foreach (var file in files) {
                await DeleteFileAsync(file.Path, cancellationToken).AnyContext();
                count++;
            }
            _logger.LogTrace("Finished deleting {FileCount} files matching {SearchPattern}", count, searchPattern);

            return count;
        }

        public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default) {
            if (pageSize <= 0)
                return PagedFileListResult.Empty;

            var result = new PagedFileListResult(_ => GetFiles(searchPattern, 1, pageSize, cancellationToken));
            await result.NextPageAsync().AnyContext();
            return result;
        }

        private async Task<NextPageResult> GetFiles(string searchPattern, int page, int pageSize, CancellationToken cancellationToken) {
            int pagingLimit = pageSize;
            int skip = (page - 1) * pagingLimit;
            if (pagingLimit < Int32.MaxValue)
                pagingLimit++;

            var list = await GetFileListAsync(searchPattern, pagingLimit, skip, cancellationToken).AnyContext();
            bool hasMore = false;
            if (list.Count == pagingLimit) {
                hasMore = true;
                list.RemoveAt(pagingLimit - 1);
            }

            return new NextPageResult {
                Success = true,
                HasMore = hasMore,
                Files = list,
                NextPageFunc = hasMore ? _ => GetFiles(searchPattern, page + 1, pageSize, cancellationToken) : null
            };
        }

        private async Task<List<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default) {
            if (limit is <= 0)
                return new List<FileSpec>();
            
            var criteria = GetRequestCriteria(searchPattern);

            int totalLimit = limit.GetValueOrDefault(Int32.MaxValue) < Int32.MaxValue 
                ? skip.GetValueOrDefault() + limit.Value
                : Int32.MaxValue;
            
            BlobContinuationToken continuationToken = null;
            var blobs = new List<CloudBlockBlob>();
            do {
                var listingResult = await _container.ListBlobsSegmentedAsync(criteria.Prefix, true, BlobListingDetails.Metadata, limit, continuationToken, null, null, cancellationToken).AnyContext();
                continuationToken = listingResult.ContinuationToken;

                foreach (var blob in listingResult.Results.OfType<CloudBlockBlob>()) {
                    if (criteria.Pattern != null && !criteria.Pattern.IsMatch(blob.Name)) {
                        _logger.LogTrace("Skipping {Path}: Doesn't match pattern", blob.Name);
                        continue;
                    }

                    blobs.Add(blob);
                }
            } while (continuationToken != null && blobs.Count < totalLimit);
            
            if (skip.HasValue)
                blobs = blobs.Skip(skip.Value).ToList();
            
            if (limit.HasValue)
                blobs = blobs.Take(limit.Value).ToList();
            
            return blobs.Select(blob => blob.ToFileInfo()).ToList();
        }
        
        private string NormalizePath(string path) {
            return path?.Replace('\\', '/');
        }
        
        private class SearchCriteria {
            public string Prefix { get; set; }
            public Regex Pattern { get; set; }
        }

        private SearchCriteria GetRequestCriteria(string searchPattern) {
            if (String.IsNullOrEmpty(searchPattern))
                return new SearchCriteria { Prefix = String.Empty };

            string normalizedSearchPattern = NormalizePath(searchPattern);
            int wildcardPos = normalizedSearchPattern.IndexOf('*');
            bool hasWildcard = wildcardPos >= 0;

            string prefix = normalizedSearchPattern;
            Regex patternRegex = null;
            
            if (hasWildcard) {
                patternRegex = new Regex($"^{Regex.Escape(normalizedSearchPattern).Replace("\\*", ".*?")}$");
                int slashPos = normalizedSearchPattern.LastIndexOf('/');
                prefix = slashPos >= 0 ? normalizedSearchPattern.Substring(0, slashPos) : String.Empty;
            }

            return new SearchCriteria {
                Prefix = prefix,
                Pattern = patternRegex
            };
        }
        
        public void Dispose() {}
    }
}
