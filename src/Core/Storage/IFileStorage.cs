using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Foundatio.Storage {
    public interface IFileStorage : IDisposable {
        string GetFileContents(string path);
        FileSpec GetFileInfo(string path);
        bool Exists(string path);
        bool SaveFile(string path, string contents);
        bool RenameFile(string oldpath, string newpath);
        bool DeleteFile(string path);
        IEnumerable<FileSpec> GetFileList(string searchPattern = null, int? limit = null);
    }

    public interface IFileStorage2 : IDisposable {
        Task<Stream> GetFileContentsAsync(string path, CancellationToken cancellationToken = default(CancellationToken));
        Task<FileSpec> GetFileInfoAsync(string path);
        Task<bool> ExistsAsync(string path);
        Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> RenameFileAsync(string path, string newpath);
        Task<bool> CopyFileAsync(string path, string targetpath);
        Task<bool> DeleteFileAsync(string path);
        // TODO: Support paging large file lists
        Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, CancellationToken cancellationToken = default(CancellationToken));
    }

    public class FileSpec {
        public string Path { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public long Size { get; set; }
        // TODO: Add metadata object for custom properties
    }

    public static class FileStorageExtensions {
        public static bool SaveObject<T>(this IFileStorage storage, string path, T data) {
            return storage.SaveFile(path, JsonConvert.SerializeObject(data));
        }

        public static T GetObject<T>(this IFileStorage storage, string path) {
            string json = storage.GetFileContents(path);
            return JsonConvert.DeserializeObject<T>(json);
        }

        public static void DeleteFiles(this IFileStorage storage, IEnumerable<FileSpec> files) {
            foreach (var file in files)
                storage.DeleteFile(file.Path);
        }
    }
}
