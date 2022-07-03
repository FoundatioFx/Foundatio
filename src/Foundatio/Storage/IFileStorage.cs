using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Storage {
    public interface IFileStorage : IHaveSerializer, IDisposable {
        Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default);
        Task<FileSpec> GetFileInfoAsync(string path);
        Task<bool> ExistsAsync(string path);
        Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default);
        Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default);
        Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default);
        Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default);
        Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default);
        Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default);
    }

    public interface IHasNextPageFunc {
        Func<PagedFileListResult, Task<NextPageResult>> NextPageFunc { get; set; }
    }

    public class NextPageResult {
        public bool Success { get; set; }
        public bool HasMore { get; set; }
        public IReadOnlyCollection<FileSpec> Files { get; set; }
        public Func<PagedFileListResult, Task<NextPageResult>> NextPageFunc { get; set; }
    }

    public class PagedFileListResult : IHasNextPageFunc {
        private static IReadOnlyCollection<FileSpec> _empty = new ReadOnlyCollection<FileSpec>(Array.Empty<FileSpec>());
        public static PagedFileListResult Empty = new(_empty);

        public PagedFileListResult(IReadOnlyCollection<FileSpec> files) {
            Files = files;
            HasMore = false;
            ((IHasNextPageFunc)this).NextPageFunc = null;
        }
        
        public PagedFileListResult(IReadOnlyCollection<FileSpec> files, bool hasMore, Func<PagedFileListResult, Task<NextPageResult>> nextPageFunc) {
            Files = files;
            HasMore = hasMore;
            ((IHasNextPageFunc)this).NextPageFunc = nextPageFunc;
        }

        public PagedFileListResult(Func<PagedFileListResult, Task<NextPageResult>> nextPageFunc) {
            ((IHasNextPageFunc)this).NextPageFunc = nextPageFunc;
        }

        public IReadOnlyCollection<FileSpec> Files { get; private set; }
        public bool HasMore { get; private set; }
        protected IDictionary<string, object> Data { get; } = new DataDictionary();
        Func<PagedFileListResult, Task<NextPageResult>> IHasNextPageFunc.NextPageFunc { get; set; }

        public async Task<bool> NextPageAsync() {
            if (((IHasNextPageFunc)this).NextPageFunc == null)
                return false;
            
            var result = await ((IHasNextPageFunc)this).NextPageFunc(this).AnyContext();
            if (result.Success) {
                Files = result.Files;
                HasMore = result.HasMore;
                ((IHasNextPageFunc)this).NextPageFunc = result.NextPageFunc;
            } else {
                Files = _empty;
                HasMore = false;
                ((IHasNextPageFunc)this).NextPageFunc = null;
            }

            return result.Success;
        }
    }

    [DebuggerDisplay("Path = {Path}, Created = {Created}, Modified = {Modified}, Size = {Size} bytes")]
    public class FileSpec {
        public string Path { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }

        /// <summary>
        /// In Bytes
        /// </summary>
        public long Size { get; set; }
        // TODO: Add metadata object for custom properties
    }

    public static class FileStorageExtensions {
        public static Task<bool> SaveObjectAsync<T>(this IFileStorage storage, string path, T data, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var bytes = storage.Serializer.SerializeToBytes(data);
            return storage.SaveFileAsync(path, new MemoryStream(bytes), cancellationToken);
        }

        public static async Task<T> GetObjectAsync<T>(this IFileStorage storage, string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            using (var stream = await storage.GetFileStreamAsync(path, cancellationToken).AnyContext()) {
                if (stream != null)
                    return storage.Serializer.Deserialize<T>(stream);
            }

            return default;
        }

        public static async Task DeleteFilesAsync(this IFileStorage storage, IEnumerable<FileSpec> files) {
            if (files == null)
                throw new ArgumentNullException(nameof(files));

            foreach (var file in files)
                await storage.DeleteFileAsync(file.Path).AnyContext();
        }

        public static async Task<string> GetFileContentsAsync(this IFileStorage storage, string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            using (var stream = await storage.GetFileStreamAsync(path).AnyContext()) {
                if (stream != null)
                    return await new StreamReader(stream).ReadToEndAsync().AnyContext();
            }

            return null;
        }

        public static async Task<byte[]> GetFileContentsRawAsync(this IFileStorage storage, string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            using var stream = await storage.GetFileStreamAsync(path).AnyContext();
            if (stream == null)
                return null;

            var buffer = new byte[16 * 1024];
            using var ms = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).AnyContext()) > 0) {
                await ms.WriteAsync(buffer, 0, read).AnyContext();
            }

            return ms.ToArray();
        }

        public static Task<bool> SaveFileAsync(this IFileStorage storage, string path, string contents) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            return storage.SaveFileAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(contents ?? String.Empty)));
        }

        public static async Task<IReadOnlyCollection<FileSpec>> GetFileListAsync(this IFileStorage storage, string searchPattern = null, int? limit = null, CancellationToken cancellationToken = default) {
            var files = new List<FileSpec>();
            limit ??= Int32.MaxValue;
            var result = await storage.GetPagedFileListAsync(limit.Value, searchPattern, cancellationToken).AnyContext();
            do {
                files.AddRange(result.Files);
            } while (result.HasMore && files.Count < limit.Value && await result.NextPageAsync().AnyContext());
            
            return files;
        }
    }
}
