﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Storage {
    public class FolderFileStorage : IFileStorage {
        private readonly object _lockObject = new object();

        public FolderFileStorage(string folder) {
            folder = PathHelper.ExpandPath(folder);

            if (!Path.IsPathRooted(folder))
                folder = Path.GetFullPath(folder);
            if (!folder.EndsWith("\\"))
                folder += "\\";

            Folder = folder;

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
        }

        public string Folder { get; set; }

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            try {
                if (!await ExistsAsync(path).AnyContext())
                    return null;

                return File.OpenRead(Path.Combine(Folder, path));
            } catch (FileNotFoundException) {
                return null;
            }
        }

        public Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

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
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            return Task.FromResult(File.Exists(Path.Combine(Folder, path)));
        }

        public Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            string directory = Path.GetDirectoryName(Path.Combine(Folder, path));
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            try {
                using (var fileStream = File.Create(Path.Combine(Folder, path))) {
                    if (stream.CanSeek)
                        stream.Seek(0, SeekOrigin.Begin);

                    stream.CopyTo(fileStream);
                }
            } catch (Exception) {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<bool> RenameFileAsync(string path, string newpath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrWhiteSpace(newpath))
                throw new ArgumentNullException(nameof(newpath));

            try {
                lock (_lockObject) {
                    string directory = Path.GetDirectoryName(newpath);
                    if (directory != null && !Directory.Exists(Path.Combine(Folder, directory)))
                        Directory.CreateDirectory(Path.Combine(Folder, directory));

                    string oldFullPath = Path.Combine(Folder, path);
                    string newFullPath = Path.Combine(Folder, newpath);
                    try {
                        File.Move(oldFullPath, newFullPath);
                    } catch (IOException) {
                        File.Delete(newFullPath);
                        File.Move(oldFullPath, newFullPath);
                    }
                }
            } catch (Exception) {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<bool> CopyFileAsync(string path, string targetpath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrWhiteSpace(targetpath))
                throw new ArgumentNullException(nameof(targetpath));

            try {
                lock (_lockObject) {
                    string directory = Path.GetDirectoryName(targetpath);
                    if (directory != null && !Directory.Exists(Path.Combine(Folder, directory)))
                        Directory.CreateDirectory(Path.Combine(Folder, directory));

                    File.Copy(Path.Combine(Folder, path), Path.Combine(Folder, targetpath));
                }
            } catch (Exception) {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            try {
                File.Delete(Path.Combine(Folder, path));
            } catch (Exception) {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task DeleteFilesAsync(string searchPattern = null, CancellationToken cancellation = default(CancellationToken)) {
            if (String.IsNullOrEmpty(searchPattern) || searchPattern == "*") {
                Directory.Delete(Folder, true);
                return Task.CompletedTask;
            }
            
            searchPattern = searchPattern.Replace("/", "\\");

            var path = Path.Combine(Folder, searchPattern);
            if (path.EndsWith("\\") || path.EndsWith("\\*")) {
                var directory = Path.GetDirectoryName(path);
                if (Directory.Exists(directory)) {
                    Directory.Delete(directory, true);
                    return Task.CompletedTask;
                }
            } else if (Directory.Exists(path)) {
                Directory.Delete(path, true);
                return Task.CompletedTask;
            }

            foreach (var file in Directory.EnumerateFiles(Folder, searchPattern, SearchOption.AllDirectories))
                File.Delete(file);

            return Task.CompletedTask;
            
        }

        public Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (limit.HasValue && limit.Value <= 0)
                return Task.FromResult<IEnumerable<FileSpec>>(new List<FileSpec>());

            if (String.IsNullOrEmpty(searchPattern))
                searchPattern = "*";

            searchPattern = searchPattern.Replace("/", "\\");

            var list = new List<FileSpec>();
            if (!Directory.Exists(Path.GetDirectoryName(Path.Combine(Folder, searchPattern))))
                return Task.FromResult<IEnumerable<FileSpec>>(list);

            foreach (var path in Directory.EnumerateFiles(Folder, searchPattern, SearchOption.AllDirectories).Skip(skip ?? 0).Take(limit ?? Int32.MaxValue)) {
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
