using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Storage {
    public class InMemoryFileStorage : IFileStorage {
        private readonly Dictionary<string, Tuple<FileSpec, byte[]>> _storage = new Dictionary<string, Tuple<FileSpec, byte[]>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        public InMemoryFileStorage() : this(1024 * 1024 * 256, 100) {}

        public InMemoryFileStorage(long maxFileSize, int maxFiles) {
            MaxFileSize = maxFileSize;
            MaxFiles = maxFiles;
        }

        public long MaxFileSize { get; set; }
        public long MaxFiles { get; set; }

        public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            lock (_lock) {
                if (!_storage.ContainsKey(path))
                    return Task.FromResult<Stream>(null);

                return Task.FromResult<Stream>(new MemoryStream(_storage[path].Item2));
            }
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
            return await ExistsAsync(path).AnyContext() ? _storage[path].Item1 : null;
        }

        public Task<bool> ExistsAsync(string path) {
            return Task.FromResult(_storage.ContainsKey(path));
        }

        private static byte[] ReadBytes(Stream input) {
            using (var ms = new MemoryStream()) {
                input.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            byte[] contents = ReadBytes(stream);
            if (contents.Length > MaxFileSize)
                throw new ArgumentException(String.Format("File size {0} exceeds the maximum size of {1}.", contents.Length.ToFileSizeDisplay(), MaxFileSize.ToFileSizeDisplay()));

            lock (_lock) {
                _storage[path] = Tuple.Create(new FileSpec {
                    Created = DateTime.Now,
                    Modified = DateTime.Now,
                    Path = path,
                    Size = contents.Length
                }, contents);

                if (_storage.Count > MaxFiles)
                    _storage.Remove(_storage.OrderByDescending(kvp => kvp.Value.Item1.Created).First().Key);
            }

            return Task.FromResult(true);
        }

        public Task<bool> RenameFileAsync(string path, string newpath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrWhiteSpace(newpath))
                throw new ArgumentNullException(nameof(newpath));

            lock (_lock) {
                if (!_storage.ContainsKey(path))
                    return Task.FromResult(false);

                _storage[newpath] = _storage[path];
                _storage[newpath].Item1.Path = newpath;
                _storage[newpath].Item1.Modified = DateTime.Now;
                _storage.Remove(path);
            }

            return Task.FromResult(true);
        }

        public Task<bool> CopyFileAsync(string path, string targetpath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrWhiteSpace(targetpath))
                throw new ArgumentNullException(nameof(targetpath));

            lock (_lock) {
                if (!_storage.ContainsKey(path))
                    return Task.FromResult(false);

                _storage[targetpath] = _storage[path];
                _storage[targetpath].Item1.Path = targetpath;
                _storage[targetpath].Item1.Modified = DateTime.Now;
            }

            return Task.FromResult(true);
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            lock (_lock) {
                if (!_storage.ContainsKey(path))
                    return Task.FromResult(false);

                _storage.Remove(path);
            }

            return Task.FromResult(true);
        }

        public Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (limit.HasValue && limit.Value <= 0)
                return Task.FromResult<IEnumerable<FileSpec>>(new List<FileSpec>());

            if (searchPattern == null)
                searchPattern = "*";

            var regex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");
            lock (_lock)
                return Task.FromResult<IEnumerable<FileSpec>>(_storage.Keys.Where(k => regex.IsMatch(k)).Select(k => _storage[k].Item1).Skip(skip ?? 0).Take(limit ?? Int32.MaxValue).ToList());
        }

        public void Dispose() {
            _storage?.Clear();
        }
    }
}