using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Storage {
    public class ScopedFileStorage : IFileStorage {
        private readonly string _pathPrefix;

        public ScopedFileStorage(IFileStorage storage, string scope) {
            UnscopedStorage = storage;
            Scope = !String.IsNullOrWhiteSpace(scope) ? scope.Trim() : null;
            _pathPrefix = Scope != null ? String.Concat(Scope, "/") : String.Empty;
        }

        public IFileStorage UnscopedStorage { get; private set; }

        public string Scope { get; private set; }
        ISerializer IHaveSerializer.Serializer => UnscopedStorage.Serializer;

        public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            return UnscopedStorage.GetFileStreamAsync(String.Concat(_pathPrefix, path), cancellationToken);
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var file = await UnscopedStorage.GetFileInfoAsync(String.Concat(_pathPrefix, path)).AnyContext();
            if (file != null)
                file.Path = file.Path.Substring(_pathPrefix.Length);

            return file;
        }

        public Task<bool> ExistsAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            return UnscopedStorage.ExistsAsync(String.Concat(_pathPrefix, path));
        }

        public Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            return UnscopedStorage.SaveFileAsync(String.Concat(_pathPrefix, path), stream, cancellationToken);
        }

        public Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            return UnscopedStorage.RenameFileAsync(String.Concat(_pathPrefix, path), String.Concat(_pathPrefix, newPath), cancellationToken);
        }

        public Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            return UnscopedStorage.CopyFileAsync(String.Concat(_pathPrefix, path), String.Concat(_pathPrefix, targetPath), cancellationToken);
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            return UnscopedStorage.DeleteFileAsync(String.Concat(_pathPrefix, path), cancellationToken);
        }

        public Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default) {
            return UnscopedStorage.DeleteFilesAsync(String.Concat(_pathPrefix, searchPattern), cancellation);
        }

        public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default) {
            if (pageSize <= 0)
                return PagedFileListResult.Empty;

            var unscopedResult = await UnscopedStorage.GetPagedFileListAsync(pageSize, String.Concat(_pathPrefix, searchPattern), cancellationToken).AnyContext();
            foreach (var file in unscopedResult.Files)
                file.Path = file.Path.Substring(_pathPrefix.Length);

            return new PagedFileListResult(unscopedResult.Files, unscopedResult.HasMore, s => NextPage(unscopedResult));
        }

        private async Task<NextPageResult> NextPage(PagedFileListResult result) {
            var success = await result.NextPageAsync().AnyContext();
            foreach (var file in result.Files)
                file.Path = file.Path.Substring(_pathPrefix.Length);
            return new NextPageResult { Success = success, HasMore = result.HasMore, Files = result.Files, NextPageFunc = s => NextPage(result) };
        }

        public void Dispose() { }
    }
}
