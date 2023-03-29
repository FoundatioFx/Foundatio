using System;
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
using StackExchange.Redis;

namespace Foundatio.Storage {
    public class RedisFileStorage : IFileStorage {
        private readonly RedisFileStorageOptions _options;
        private readonly ISerializer _serializer;
        private readonly ILogger _logger;
        private readonly string _fileSpecContainer;

        public RedisFileStorage(RedisFileStorageOptions options) {
            if (options.ConnectionMultiplexer == null)
                throw new ArgumentException("ConnectionMultiplexer is required.");
            
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
            
            options.ConnectionMultiplexer.ConnectionRestored += ConnectionMultiplexerOnConnectionRestored;
            _fileSpecContainer = $"{options.ContainerName}-filespecs";
            _options = options;
        }

        public RedisFileStorage(Builder<RedisFileStorageOptionsBuilder, RedisFileStorageOptions> config)
            : this(config(new RedisFileStorageOptionsBuilder()).Build()) { }

        ISerializer IHaveSerializer.Serializer => _serializer;

        private IDatabase Database => _options.ConnectionMultiplexer.GetDatabase();

        public void Dispose() {
            _options.ConnectionMultiplexer.ConnectionRestored -= ConnectionMultiplexerOnConnectionRestored;
        }

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            
            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Getting file stream for {Path}", normalizedPath);
            
            var fileContent = await Run.WithRetriesAsync(() => Database.HashGetAsync(_options.ContainerName, normalizedPath),
                cancellationToken: cancellationToken, logger: _logger).AnyContext();
            
            if (fileContent.IsNull) {
                _logger.LogError("Unable to get file stream for {Path}: File Not Found", normalizedPath);
                return null;
            }
            
            return new MemoryStream(fileContent);
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            
            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Getting file info for {Path}", normalizedPath);
            
            var fileSpec = await Run.WithRetriesAsync(() => Database.HashGetAsync(_fileSpecContainer, normalizedPath), logger: _logger).AnyContext();
            if (!fileSpec.HasValue) {
                _logger.LogError("Unable to get file info for {Path}: File Not Found", normalizedPath);
                return null;
            }
            
            return _serializer.Deserialize<FileSpec>((byte[])fileSpec);
        }

        public Task<bool> ExistsAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            
            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Checking if {Path} exists", normalizedPath);
            
            return Run.WithRetriesAsync(() => Database.HashExistsAsync(_fileSpecContainer, normalizedPath), logger: _logger);
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            
            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Saving {Path}", normalizedPath);
            
            try {
                var database = Database;
                
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, 0x14000, cancellationToken).AnyContext();
                var saveFileTask = database.HashSetAsync(_options.ContainerName, normalizedPath, memory.ToArray());
                long fileSize = memory.Length;
                memory.Seek(0, SeekOrigin.Begin);
                memory.SetLength(0);
                
                _serializer.Serialize(new FileSpec {
                    Path = normalizedPath,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    Size = fileSize
                }, memory);
                var saveSpecTask = database.HashSetAsync(_fileSpecContainer, normalizedPath, memory.ToArray());
                await Run.WithRetriesAsync(() => Task.WhenAll(saveFileTask, saveSpecTask),
                    cancellationToken: cancellationToken, logger: _logger).AnyContext();
                return true;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error saving {Path}: {Message}", normalizedPath, ex.Message);
                return false;
            }
        }

        public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));
            
            string normalizedPath = NormalizePath(path);
            string normalizedNewPath = NormalizePath(newPath);
            _logger.LogInformation("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);
            
            try {
                var stream = await GetFileStreamAsync(normalizedPath, cancellationToken).AnyContext();
                return await DeleteFileAsync(normalizedPath, cancellationToken).AnyContext() &&
                       await SaveFileAsync(normalizedNewPath, stream, cancellationToken).AnyContext();
            } catch (Exception ex) {
                _logger.LogError(ex, "Error renaming {Path} to {NewPath}: {Message}", normalizedPath, newPath, ex.Message);
                return false;
            }
        }

        public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));
            
            string normalizedPath = NormalizePath(path);
            string normalizedTargetPath = NormalizePath(targetPath);
            _logger.LogInformation("Copying {Path} to {TargetPath}", normalizedPath, normalizedTargetPath);
            
            try {
                using var stream = await GetFileStreamAsync(normalizedPath, cancellationToken).AnyContext();
                if (stream == null)
                    return false;

                return await SaveFileAsync(normalizedTargetPath, stream, cancellationToken).AnyContext();
            } catch (Exception ex) {
                _logger.LogError(ex, "Error copying {Path} to {TargetPath}: {Message}", normalizedPath, normalizedTargetPath, ex.Message);
                return false;
            }
        }

        public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            
            string normalizedPath = NormalizePath(path);
            _logger.LogTrace("Deleting {Path}", normalizedPath);

            var database = Database;
            var deleteSpecTask = database.HashDeleteAsync(_fileSpecContainer, normalizedPath);
            var deleteFileTask = database.HashDeleteAsync(_options.ContainerName, normalizedPath);
            await Run.WithRetriesAsync(() => Task.WhenAll(deleteSpecTask, deleteFileTask), cancellationToken: cancellationToken, logger: _logger).AnyContext();
            return true;
        }

        public async Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = default) {
            var files = await GetFileListAsync(searchPattern, cancellationToken: cancellationToken).AnyContext();
            int count = 0;

            _logger.LogInformation("Deleting {FileCount} files matching {SearchPattern}", files, searchPattern);
            foreach (var file in files) {
                await DeleteFileAsync(file.Path, cancellationToken).AnyContext();
                count++;
            }
            _logger.LogTrace("Finished deleting {FileCount} files matching {SearchPattern}", count, searchPattern);
            
            return count;
        }

        private Task<List<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default) {
            if (limit is <= 0)
                return Task.FromResult(new List<FileSpec>());
            
            searchPattern = NormalizePath(searchPattern);
            string prefix = searchPattern;
            Regex patternRegex = null;
            int wildcardPos = searchPattern?.IndexOf('*') ?? -1;
            if (searchPattern != null && wildcardPos >= 0) {
                patternRegex = new Regex($"^{Regex.Escape(searchPattern).Replace("\\*", ".*?")}$");
                int slashPos = searchPattern.LastIndexOf('/');
                prefix = slashPos >= 0 ? searchPattern.Substring(0, slashPos) : String.Empty;
            }
            
            prefix ??= String.Empty;
            int pageSize = limit ?? Int32.MaxValue;
            
            _logger.LogTrace(
                s => s.Property("SearchPattern", searchPattern).Property("Limit", limit).Property("Skip", skip), 
                "Getting file list matching {Prefix} and {Pattern}...", prefix, patternRegex
            );
            
            return Task.FromResult(Database.HashScan(_fileSpecContainer, $"{prefix}*")
                .Select(entry => _serializer.Deserialize<FileSpec>((byte[])entry.Value))
                .Where(fileSpec => patternRegex == null || patternRegex.IsMatch(fileSpec.Path))
                .Take(pageSize)
                .ToList()
            );
        }

        public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default) {
            if (pageSize <= 0)
                return PagedFileListResult.Empty;

            var criteria = GetRequestCriteria(searchPattern);
            var result = new PagedFileListResult(r => Task.FromResult(GetFiles(criteria, 1, pageSize)));
            await result.NextPageAsync().AnyContext();
            return result;
        }

        private NextPageResult GetFiles(SearchCriteria criteria, int page, int pageSize) {
            int pagingLimit = pageSize;
            int skip = (page - 1) * pagingLimit;
            if (pagingLimit < Int32.MaxValue)
                pagingLimit++;
            
            _logger.LogTrace(
                s => s.Property("Limit", pagingLimit).Property("Skip", skip), 
                "Getting files matching {Prefix} and {Pattern}...", criteria.Prefix, criteria.Pattern
            );
            
            var list = Database.HashScan(_fileSpecContainer, $"{criteria.Prefix}*")
                .Select(entry => _serializer.Deserialize<FileSpec>((byte[])entry.Value))
                .Where(fileSpec => criteria.Pattern == null || criteria.Pattern.IsMatch(fileSpec.Path))
                .Skip(skip)
                .Take(pagingLimit)
                .ToList();

            bool hasMore = false;
            if (list.Count == pagingLimit) {
                hasMore = true;
                list.RemoveAt(pagingLimit - 1);
            }

            return new NextPageResult {
                Success = true,
                HasMore = hasMore,
                Files = list,
                NextPageFunc = hasMore ? _ => Task.FromResult(GetFiles(criteria, page + 1, pageSize)) : null
            };
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

        private void ConnectionMultiplexerOnConnectionRestored(object sender, ConnectionFailedEventArgs connectionFailedEventArgs) {
            _logger.LogInformation("Redis connection restored");
        }
    }
}
