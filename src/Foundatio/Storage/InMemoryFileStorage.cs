using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Extensions;
using Foundatio.Serializer;

namespace Foundatio.Storage {
    public class InMemoryFileStorage : IWritableStream {
        private readonly Dictionary<string, Tuple<FileSpec, byte[]>> _storage = new (StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _semaphore = new (initialCount: 1, maxCount: 1);

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

        class RegisteringMemoryStream : MemoryStream, IDisposable {
            string _path;
            InMemoryFileStorage _memoryStorage;
            public RegisteringMemoryStream(InMemoryFileStorage storage, string path) {
                _memoryStorage = storage;
                _path = path;
            }

            void IDisposable.Dispose() {
                if (Length > 0) {
                    var contents = ToArray();
                    var entry = Tuple.Create(new FileSpec {
                            Created = SystemClock.UtcNow,
                            Modified = SystemClock.UtcNow,
                            Path = _path,
                            Size = contents.Length
                        }, contents);

                    _memoryStorage.Lock();
                    try {
                        _memoryStorage._storage[_path] = entry;

                        if (_memoryStorage._storage.Count > _memoryStorage.MaxFiles)
                            _memoryStorage._storage.Remove(_memoryStorage._storage.OrderByDescending(kvp => kvp.Value.Item1.Created).First().Key);
                    } finally {
                        _memoryStorage.Unlock();
                    }
                }

                base.Dispose();
            }
        }

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();

            await LockAsync(cancellationToken);
            try {
                return !_storage.ContainsKey(path)
                    ? null 
                    : new MemoryStream(_storage[path].Item2);
            } finally {
                Unlock();
            }
        }

        public Task<Stream> GetWritableStreamAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();
            return Task.FromResult<Stream>(new RegisteringMemoryStream(this, path));
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

        private static async ValueTask<byte[]> ReadBytesAsync(Stream input) {
            using var ms = new MemoryStream();

            await input.CopyToAsync(ms);
            return ms.ToArray();
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            path = path.NormalizePath();
            var contents = await ReadBytesAsync(stream);
            if (contents.Length > MaxFileSize)
                throw new ArgumentException($"File size {contents.Length.ToFileSizeDisplay()} exceeds the maximum size of {MaxFileSize.ToFileSizeDisplay()}.");

            var entry = Tuple.Create(new FileSpec {
                    Created = SystemClock.UtcNow,
                    Modified = SystemClock.UtcNow,
                    Path = path,
                    Size = contents.Length
                }, contents);

            await LockAsync(cancellationToken);
            try {
               _storage[path] = entry;

                if (_storage.Count > MaxFiles)
                    _storage.Remove(_storage.OrderByDescending(kvp => kvp.Value.Item1.Created).First().Key);
            } finally {
                Unlock();
            }

            return true;
        }

        public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            path = path.NormalizePath();
            newPath = newPath.NormalizePath();

            await LockAsync(cancellationToken);
            try {
                if (!_storage.TryGetValue(path, out var entry)) {
                    return false;
                }

                Debug.Assert(entry != null);

                entry.Item1.Path = newPath;
                entry.Item1.Modified = SystemClock.UtcNow;

                _storage[newPath] = entry;

                _storage.Remove(path);
            } finally {
                Unlock();
            }

            return true;
        }

        public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            path = path.NormalizePath();
            targetPath = targetPath.NormalizePath();

            await LockAsync(cancellationToken);
            try {
                if (!_storage.TryGetValue(path, out var entry))
                    return false;

                var newEntry = Tuple.Create(new FileSpec {
                    Created = entry.Item1.Created,
                    Modified = SystemClock.UtcNow,
                    Path = targetPath,
                    Size = entry.Item1.Size
                }, entry.Item2);

                _storage[targetPath] = newEntry;

            } finally {
                Unlock();
            }

            return true;
        }

        public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();

            await LockAsync(cancellationToken);
            try {
                return _storage.Remove(path);
            } finally {
                Unlock();
            }
        }

        public async Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(searchPattern) || searchPattern == "*") {
                await LockAsync(cancellationToken);
                try {
                    _storage.Clear();
                } finally {
                    Unlock();
                }

                return 0;
            }

            searchPattern = searchPattern.NormalizePath();
            int count = 0;

            if (searchPattern[searchPattern.Length - 1] == Path.DirectorySeparatorChar) 
                searchPattern = $"{searchPattern}*";
            else if (!searchPattern.EndsWith(Path.DirectorySeparatorChar + "*") && !Path.HasExtension(searchPattern))
                searchPattern = Path.Combine(searchPattern, "*");

            var regex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");
            await LockAsync(cancellationToken);
            try {
                var keys = _storage.Keys.Where(k => regex.IsMatch(k))
                                        .Select(k => _storage[k].Item1)
                                        .ToList();

                foreach (var key in keys) {
                    _storage.Remove(key.Path);
                    count++;
                }
            } finally {
                Unlock();
            }

            return count;
        }

        public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default) {
            if (pageSize <= 0)
                return PagedFileListResult.Empty;

            if (searchPattern == null)
                searchPattern = "*";

            searchPattern = searchPattern.NormalizePath();

            var result = new PagedFileListResult(s => GetFilesAsync(searchPattern, 1, pageSize, cancellationToken));
            await result.NextPageAsync().AnyContext();
            return result;
        }

        private async Task<NextPageResult> GetFilesAsync(string searchPattern, int page, int pageSize, CancellationToken cancellationToken) {
            var list = new List<FileSpec>();
            int pagingLimit = pageSize;
            int skip = (page - 1) * pagingLimit;
            if (pagingLimit < Int32.MaxValue)
                pagingLimit += 1;
            
            var regex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");


            await LockAsync(cancellationToken);
            try {
                list.AddRange(_storage.Keys.Where(k => regex.IsMatch(k))
                                           .Select(k => _storage[k].Item1)
                                           .Skip(skip)
                                           .Take(pagingLimit)
                                           .ToList());
            } finally {
                Unlock();
            }
            
            bool hasMore = false;
            if (list.Count == pagingLimit) {
                hasMore = true;
                list.RemoveAt(pagingLimit - 1);
            }

            return new NextPageResult {
                Success = true, 
                HasMore = hasMore, 
                Files = list,
                NextPageFunc = hasMore ? s => GetFilesAsync(searchPattern, page + 1, pageSize, cancellationToken) : null 
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ConfiguredTaskAwaitable LockAsync(CancellationToken cancellationToken)
            => _semaphore.WaitAsync(cancellationToken).AnyContext();

        private void Lock()
            => _semaphore.Wait();

        private void Unlock()
            => _semaphore.Release();

        public void Dispose() {
            Lock();
            try {
                _storage?.Clear();
            } finally {
                Unlock();
            }
        }
    }
}