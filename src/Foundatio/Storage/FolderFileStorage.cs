using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Storage {
    public class FolderFileStorage : IFileStorage {
        private readonly object _lockObject = new();
        private readonly ISerializer _serializer;
        protected readonly ILogger _logger;

        public FolderFileStorage(FolderFileStorageOptions options) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger<FolderFileStorage>.Instance;

            string folder = PathHelper.ExpandPath(options.Folder);
            if (!Path.IsPathRooted(folder))
                folder = Path.GetFullPath(folder);

            char lastCharacter = folder[folder.Length - 1];
            if (!lastCharacter.Equals(Path.DirectorySeparatorChar) && !lastCharacter.Equals(Path.AltDirectorySeparatorChar))
                folder += Path.DirectorySeparatorChar;

            Folder = folder;
            Directory.CreateDirectory(folder);
        }

        public FolderFileStorage(Builder<FolderFileStorageOptionsBuilder, FolderFileStorageOptions> config) 
            : this(config(new FolderFileStorageOptionsBuilder()).Build()) { }

        public string Folder { get; set; }
        ISerializer IHaveSerializer.Serializer => _serializer;

        public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();

            try {
                return Task.FromResult<Stream>(File.OpenRead(Path.Combine(Folder, path)));
            } catch (IOException ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException) {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace(ex, "Error trying to get file stream: {Path}", path);
                return Task.FromResult<Stream>(null);
            }
        }

        public Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();

            var info = new FileInfo(Path.Combine(Folder, path));
            if (!info.Exists)
                return Task.FromResult<FileSpec>(null);

            return Task.FromResult(new FileSpec {
                Path = path.Replace(Folder, String.Empty),
                Created = info.CreationTimeUtc,
                Modified = info.LastWriteTimeUtc,
                Size = info.Length
            });
        }

        public Task<bool> ExistsAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();
            return Task.FromResult(File.Exists(Path.Combine(Folder, path)));
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            path = path.NormalizePath();
            string file = Path.Combine(Folder, path);

            try {
                using var fileStream = CreateFileStream(file);
                await stream.CopyToAsync(fileStream).AnyContext();
                return true;
            } catch (Exception ex) {
                _logger.LogError(ex, "Error trying to save file: {Path}", path);
                return false;
            }
        }

        private Stream CreateFileStream(string filePath) {
            try {
                return File.Create(filePath);
            } catch (DirectoryNotFoundException) { }

            string directory = Path.GetDirectoryName(filePath);
            if (directory != null)
                Directory.CreateDirectory(directory);

            return File.Create(filePath);
        }

        public Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            path = path.NormalizePath();
            newPath = newPath.NormalizePath();

            try {
                lock (_lockObject) {
                    string directory = Path.GetDirectoryName(newPath);
                    if (directory != null)
                        Directory.CreateDirectory(Path.Combine(Folder, directory));

                    string oldFullPath = Path.Combine(Folder, path);
                    string newFullPath = Path.Combine(Folder, newPath);
                    try {
                        File.Move(oldFullPath, newFullPath);
                    } catch (IOException) {
                        File.Delete(newFullPath);
                        File.Move(oldFullPath, newFullPath);
                    }
                }
            } catch (Exception ex) {
                _logger.LogError(ex, "Error trying to rename file {Path} to {NewPath}.", path, newPath);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            path = path.NormalizePath();

            try {
                lock (_lockObject) {
                    string directory = Path.GetDirectoryName(targetPath);
                    if (directory != null)
                        Directory.CreateDirectory(Path.Combine(Folder, directory));

                    File.Copy(Path.Combine(Folder, path), Path.Combine(Folder, targetPath));
                }
            } catch (Exception ex) {
                _logger.LogError(ex, "Error trying to copy file {Path} to {TargetPath}.", path, targetPath);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();

            try {
                File.Delete(Path.Combine(Folder, path));
            } catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException) {
                _logger.LogDebug(ex, "Error trying to delete file: {Path}.", path);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default) {
            int count = 0;
            
            if (searchPattern == null || String.IsNullOrEmpty(searchPattern) || searchPattern == "*") {
                if (Directory.Exists(Folder)) {
                    count += Directory.EnumerateFiles(Folder, "*,*", SearchOption.AllDirectories).Count();
                    Directory.Delete(Folder, true);
                }

                return Task.FromResult(count);
            }

            searchPattern = searchPattern.NormalizePath();
            string path = Path.Combine(Folder, searchPattern);
            if (path[path.Length - 1] == Path.DirectorySeparatorChar || path.EndsWith(Path.DirectorySeparatorChar + "*")) {
                string directory = Path.GetDirectoryName(path);
                if (Directory.Exists(directory)) {
                    count += Directory.EnumerateFiles(directory, "*,*", SearchOption.AllDirectories).Count();
                    Directory.Delete(directory, true);
                    return Task.FromResult(count);
                }

                return Task.FromResult(0);
            }

            if (Directory.Exists(path)) {
                count += Directory.EnumerateFiles(path, "*,*", SearchOption.AllDirectories).Count();
                Directory.Delete(path, true);
                return Task.FromResult(count);
            }

            foreach (string file in Directory.EnumerateFiles(Folder, searchPattern, SearchOption.AllDirectories)) {
                File.Delete(file);
                count++;
            }

            return Task.FromResult(count);
            
        }

        public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default) {
            if (pageSize <= 0)
                return PagedFileListResult.Empty;

            if (searchPattern == null || String.IsNullOrEmpty(searchPattern))
                searchPattern = "*";

            searchPattern = searchPattern.NormalizePath();

            var list = new List<FileSpec>();
            if (!Directory.Exists(Path.GetDirectoryName(Path.Combine(Folder, searchPattern))))
                return PagedFileListResult.Empty;

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

            foreach (string path in Directory.EnumerateFiles(Folder, searchPattern, SearchOption.AllDirectories).Skip(skip).Take(pagingLimit)) {
                var info = new FileInfo(path);
                if (!info.Exists)
                    continue;

                list.Add(new FileSpec {
                    Path = info.FullName.Replace(Folder, String.Empty),
                    Created = info.CreationTimeUtc,
                    Modified = info.LastWriteTimeUtc,
                    Size = info.Length
                });
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
                NextPageFunc = hasMore ? s => Task.FromResult(GetFiles(searchPattern, page + 1, pageSize)) : (Func<PagedFileListResult, Task<NextPageResult>>)null 
            };
        }

        public void Dispose() { }
    }
}
