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
        private readonly ILogger _logger;
        private readonly string _fileSpecContainer;

        public RedisFileStorage(RedisFileStorageOptions options) {
            if (options.ConnectionMultiplexer == null)
                throw new ArgumentException("ConnectionMultiplexer is required.");
            options.Serializer = options.Serializer ?? DefaultSerializer.Instance;
            _logger = options.LoggerFactory?.CreateLogger(typeof(RedisFileStorage)) ?? NullLogger.Instance;
            options.ConnectionMultiplexer.ConnectionRestored += ConnectionMultiplexerOnConnectionRestored;
            _fileSpecContainer = $"{options.ContainerName}-filespecs";
            _options = options;
        }

        public RedisFileStorage(Builder<RedisFileStorageOptionsBuilder, RedisFileStorageOptions> config)
            : this(config(new RedisFileStorageOptionsBuilder()).Build()) { }

        public ISerializer Serializer => _options.Serializer;

        private IDatabase Database => _options.ConnectionMultiplexer.GetDatabase();

        public void Dispose() {
            _options.ConnectionMultiplexer.ConnectionRestored -= ConnectionMultiplexerOnConnectionRestored;
        }

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = new CancellationToken()) {
            var fileContent = await Run.WithRetriesAsync(() => Database.HashGetAsync(_options.ContainerName, NormalizePath(path)),
                cancellationToken: cancellationToken, logger: _logger).AnyContext();
            if (fileContent.IsNull) return null;
            return new MemoryStream(fileContent);
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
            var fileSpec = await Run.WithRetriesAsync(() => Database.HashGetAsync(_fileSpecContainer, NormalizePath(path)), logger: _logger).AnyContext();
            if (!fileSpec.HasValue) return null;
            return Serializer.Deserialize<FileSpec>((byte[])fileSpec);
        }

        public Task<bool> ExistsAsync(string path) {
            return Run.WithRetriesAsync(() => Database.HashExistsAsync(_fileSpecContainer, NormalizePath(path)), logger: _logger);
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = new CancellationToken()) {
            path = NormalizePath(path);
            try {
                var tx = Database.CreateTransaction();
                using (var memory = new MemoryStream()) {
                    await stream.CopyToAsync(memory, 0x14000, cancellationToken).AnyContext();
                    tx.HashSetAsync(_options.ContainerName, path, memory.ToArray());
                    var fileSize = memory.Length;
                    memory.Seek(0, SeekOrigin.Begin);
                    memory.SetLength(0);
                    Serializer.Serialize(new FileSpec {
                        Path = path,
                        Created = DateTime.UtcNow,
                        Modified = DateTime.UtcNow,
                        Size = fileSize
                    }, memory);
                    tx.HashSetAsync(_fileSpecContainer, path, memory.ToArray());
                }
                return await Run.WithRetriesAsync(() => tx.ExecuteAsync(),
                    cancellationToken: cancellationToken, logger: _logger).AnyContext();
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error trying to save file: {Path}", path);
                return false;
            }
        }

        public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = new CancellationToken()) {
            path = NormalizePath(path);
            try {
                var fileStream = await GetFileStreamAsync(path, cancellationToken).AnyContext();
                return await DeleteFileAsync(path, cancellationToken).AnyContext() &&
                       await SaveFileAsync(NormalizePath(newPath), fileStream, cancellationToken).AnyContext();
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error trying to rename file {Path} to {NewPath}.", path, newPath);
                return false;
            }
        }

        public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = new CancellationToken()) {
            try {
                var file = await GetFileStreamAsync(NormalizePath(path), cancellationToken).AnyContext();
                if (file == null) return false;
                await SaveFileAsync(NormalizePath(targetPath), file, cancellationToken).AnyContext();
                return true;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error trying to copy file {Path} to {TargetPath}.", path, targetPath);
                return false;
            }
        }

        public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = new CancellationToken()) {
            path = NormalizePath(path);
            var tx = Database.CreateTransaction();
            tx.AddCondition(Condition.HashExists(_fileSpecContainer, path));
            tx.HashDeleteAsync(_fileSpecContainer, path);
            tx.HashDeleteAsync(_options.ContainerName, path);
            return (await Run.WithRetriesAsync(() => tx.ExecuteAsync(), cancellationToken: cancellationToken, logger: _logger).AnyContext());
        }

        public async Task DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = new CancellationToken()) {
            var files = await GetFileListAsync(searchPattern, cancellationToken: cancellationToken).AnyContext();
            foreach (var file in files)
                await DeleteFileAsync(file.Path, cancellationToken).AnyContext();
        }

        public Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null,
            CancellationToken cancellationToken = new CancellationToken()) {
            if (limit.HasValue && limit.Value <= 0)
                return Task.FromResult<IEnumerable<FileSpec>>(new List<FileSpec>());
            searchPattern = NormalizePath(searchPattern);
            string prefix = searchPattern;
            Regex patternRegex = null;
            int wildcardPos = searchPattern?.IndexOf('*') ?? -1;
            if (searchPattern != null && wildcardPos >= 0) {
                patternRegex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");
                int slashPos = searchPattern.LastIndexOf('/');
                prefix = slashPos >= 0 ? searchPattern.Substring(0, slashPos) : String.Empty;
            }
            prefix = prefix ?? String.Empty;
            var pageSize = limit ?? int.MaxValue;
            return Task.FromResult(Database.HashScan(_fileSpecContainer, prefix + "*")
                .Select(entry => Serializer.Deserialize<FileSpec>((byte[])entry.Value))
                .Where(fileSpec => patternRegex == null || patternRegex.IsMatch(fileSpec.Path))
                .Take(pageSize));
        }

        private string NormalizePath(string path) {
            return path?.Replace('\\', '/');
        }

        private void ConnectionMultiplexerOnConnectionRestored(object sender, ConnectionFailedEventArgs connectionFailedEventArgs) {
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Redis connection restored.");
        }
    }
}
