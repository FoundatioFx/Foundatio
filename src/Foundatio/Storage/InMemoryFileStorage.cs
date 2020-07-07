using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Extensions;
using Foundatio.Serializer;

namespace Foundatio.Storage {
    public class InMemoryFileStorage : IFileStorage {
        private readonly Dictionary<string, Tuple<FileSpec, byte[]>> _storage = new Dictionary<string, Tuple<FileSpec, byte[]>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private readonly ISerializer _serializer;

        public InMemoryFileStorage() : this(o => o) {}

        public InMemoryFileStorage(InMemoryFileStorageOptions options) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            MaxFileSize = options.MaxFileSize;
            MaxFiles = options.MaxFiles;
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
        }

        public InMemoryFileStorage(Builder<InMemoryFileStorageOptionsBuilder, InMemoryFileStorageOptions> config) 
            : this(config(new InMemoryFileStorageOptionsBuilder()).Build()) { }

        public long MaxFileSize { get; set; }
        public long MaxFiles { get; set; }
        ISerializer IHaveSerializer.Serializer => _serializer;

        public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();
            lock (_lock) {
                if (!_storage.ContainsKey(path))
                    return Task.FromResult<Stream>(null);

                return Task.FromResult<Stream>(new MemoryStream(_storage[path].Item2));
            }
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();
            return await ExistsAsync(path).AnyContext() ? _storage[path].Item1 : null;
        }

        public Task<bool> ExistsAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();
            return Task.FromResult(_storage.ContainsKey(path));
        }

        private static byte[] ReadBytes(Stream input) {
            using (var ms = new MemoryStream()) {
                input.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            path = path.NormalizePath();
            var contents = ReadBytes(stream);
            if (contents.Length > MaxFileSize)
                throw new ArgumentException(String.Format("File size {0} exceeds the maximum size of {1}.", contents.Length.ToFileSizeDisplay(), MaxFileSize.ToFileSizeDisplay()));

            lock (_lock) {
                _storage[path] = Tuple.Create(new FileSpec {
                    Created = SystemClock.UtcNow,
                    Modified = SystemClock.UtcNow,
                    Path = path,
                    Size = contents.Length
                }, contents);

                if (_storage.Count > MaxFiles)
                    _storage.Remove(_storage.OrderByDescending(kvp => kvp.Value.Item1.Created).First().Key);
            }

            return Task.FromResult(true);
        }

        public Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            path = path.NormalizePath();
            newPath = newPath.NormalizePath();
            lock (_lock) {
                if (!_storage.ContainsKey(path))
                    return Task.FromResult(false);

                _storage[newPath] = _storage[path];
                _storage[newPath].Item1.Path = newPath;
                _storage[newPath].Item1.Modified = SystemClock.UtcNow;
                _storage.Remove(path);
            }

            return Task.FromResult(true);
        }

        public Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            path = path.NormalizePath();
            targetPath = targetPath.NormalizePath();
            lock (_lock) {
                if (!_storage.ContainsKey(path))
                    return Task.FromResult(false);

                _storage[targetPath] = _storage[path];
                _storage[targetPath].Item1.Path = targetPath;
                _storage[targetPath].Item1.Modified = SystemClock.UtcNow;
            }

            return Task.FromResult(true);
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();
            lock (_lock) {
                if (!_storage.ContainsKey(path))
                    return Task.FromResult(false);

                _storage.Remove(path);
            }

            return Task.FromResult(true);
        }

        public Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default) {
            if (String.IsNullOrEmpty(searchPattern) || searchPattern == "*") {
                lock(_lock)
                    _storage.Clear();

                return Task.FromResult(0);
            }

            searchPattern = searchPattern.NormalizePath();
            int count = 0;

            if (searchPattern[searchPattern.Length - 1] == Path.DirectorySeparatorChar) 
                searchPattern = $"{searchPattern}*";
            else if (!searchPattern.EndsWith(Path.DirectorySeparatorChar + "*") && !Path.HasExtension(searchPattern))
                searchPattern = Path.Combine(searchPattern, "*");

            var regex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");
            lock (_lock) {
                var keys = _storage.Keys.Where(k => regex.IsMatch(k)).Select(k => _storage[k].Item1).ToList();
                foreach (var key in keys) {
                    _storage.Remove(key.Path);
                    count++;
                }
            }

            return Task.FromResult(count);
        }

        public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default) {
            if (pageSize <= 0)
                return PagedFileListResult.Empty;

            if (searchPattern == null)
                searchPattern = "*";

            searchPattern = searchPattern.NormalizePath();

            var result = new PagedFileListResult(s => Task.FromResult(GetFiles(searchPattern, 1, pageSize)));
            await result.NextPageAsync().AnyContext();
            return result;
        }

        private NextPageResult GetFiles(string searchPattern, int page, int pageSize) {
            var list = new List<FileSpec>();
            int pagingLimit = pageSize;
            int skip = (page - 1) * pagingLimit;
            if (pagingLimit < Int32.MaxValue)
                pagingLimit += 1;
            
            var regex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");

            lock (_lock)
                list.AddRange(_storage.Keys.Where(k => regex.IsMatch(k)).Select(k => _storage[k].Item1).Skip(skip).Take(pagingLimit).ToList());
            
            bool hasMore = false;
            if (list.Count == pagingLimit) {
                hasMore = true;
                list.RemoveAt(pagingLimit - 1);
            }

            return new NextPageResult {
                Success = true, 
                HasMore = hasMore, 
                Files = list,
                NextPageFunc = hasMore ? s => Task.FromResult(GetFiles(searchPattern, page + 1, pageSize)) : (Func<PagedFileListResult, Task<NextPageResult>>)null 
            };
        }

        public void Dispose() {
            _storage?.Clear();
        }
    }
}