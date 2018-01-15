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
        private readonly object _lockObject = new object();
        private readonly ISerializer _serializer;
        protected readonly ILogger _logger;

        public FolderFileStorage(FolderFileStorageOptions options) {
            if (options == null) {
                throw new ArgumentNullException(nameof(options));
            }
            var folder = PathHelper.ExpandPath(options.Folder);
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger<FolderFileStorage>.Instance;

            if (!Path.IsPathRooted(folder))
                folder = Path.GetFullPath(folder);

            char lastCharacter = folder[folder.Length - 1];
            if (!lastCharacter.Equals(Path.DirectorySeparatorChar) && !lastCharacter.Equals(Path.AltDirectorySeparatorChar))
                folder += Path.DirectorySeparatorChar;

            Folder = folder;
            Directory.CreateDirectory(folder);
        }

        public string Folder { get; set; }
        ISerializer IHaveSerializer.Serializer => _serializer;

        public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            path = path.NormalizePath();

            try {
                return Task.FromResult<Stream>(File.OpenRead(Path.Combine(Folder, path)));
            } catch (IOException ex) when(ex is FileNotFoundException || ex is DirectoryNotFoundException) {
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

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            path = path.NormalizePath();

            string file = Path.Combine(Folder, path);
            string directory = Path.GetDirectoryName(file);

            Directory.CreateDirectory(directory);

            try {
                using (var fileStream = File.Create(file)) {
                    await stream.CopyToAsync(fileStream).AnyContext();
                    return true;
                }
            } catch (Exception ex) {
                _logger.LogError(ex, "Error trying to save file: {Path}", path);
                return false;
            }
        }

        public Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default(CancellationToken)) {
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

        public Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default(CancellationToken)) {
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
                _logger.LogError(ex, "Error trying to copy file {Path} to {TargetPath}.", path, targetpath);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
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

        public Task DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default(CancellationToken)) {
            if (searchPattern == null || String.IsNullOrEmpty(searchPattern) || searchPattern == "*") {
                Directory.Delete(Folder, true);
                return Task.CompletedTask;
            }

            searchPattern = searchPattern.NormalizePath();

            string path = Path.Combine(Folder, searchPattern);
            if (path[path.Length - 1] == Path.DirectorySeparatorChar || path.EndsWith(Path.DirectorySeparatorChar + "*")) {
                string directory = Path.GetDirectoryName(path);
                if (Directory.Exists(directory)) {
                    Directory.Delete(directory, true);
                    return Task.CompletedTask;
                }
            } else if (Directory.Exists(path)) {
                Directory.Delete(path, true);
                return Task.CompletedTask;
            }

            foreach (string file in Directory.EnumerateFiles(Folder, searchPattern, SearchOption.AllDirectories))
                File.Delete(file);

            return Task.CompletedTask;
            
        }

        public Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (limit.HasValue && limit.Value <= 0)
                return Task.FromResult<IEnumerable<FileSpec>>(new List<FileSpec>());

            if (searchPattern == null || String.IsNullOrEmpty(searchPattern))
                searchPattern = "*";
            
            searchPattern = searchPattern.NormalizePath();

            var list = new List<FileSpec>();
            if (!Directory.Exists(Path.GetDirectoryName(Path.Combine(Folder, searchPattern))))
                return Task.FromResult<IEnumerable<FileSpec>>(list);

            foreach (string path in Directory.EnumerateFiles(Folder, searchPattern, SearchOption.AllDirectories).Skip(skip ?? 0).Take(limit ?? Int32.MaxValue)) {
                var info = new FileInfo(path);
                if (!info.Exists)
                    continue;

                list.Add(new FileSpec {
                    Path = path.Replace(Folder, String.Empty),
                    Created = info.CreationTimeUtc,
                    Modified = info.LastWriteTimeUtc,
                    Size = info.Length
                });
            }

            return Task.FromResult<IEnumerable<FileSpec>>(list);
        }

        public void Dispose() { }
    }
}
