using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken));
        Task<FileSpec> GetFileInfoAsync(string path);
        Task<bool> ExistsAsync(string path);
        Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> RenameFileAsync(string path, string newpath, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> CopyFileAsync(string path, string targetpath, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken));
        Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default(CancellationToken));
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
            if (String.IsNullOrEmpty(json))
                return default(T);

            return JsonConvert.DeserializeObject<T>(json);
        }

        public static void DeleteFiles(this IFileStorage storage, IEnumerable<FileSpec> files) {
            foreach (var file in files)
                storage.DeleteFile(file.Path);
        }
    }

    public static class FileStorage2Extensions {
        public static bool SaveObject<T>(this IFileStorage2 storage, string path, T data) {
            return storage.SaveFile(path, JsonConvert.SerializeObject(data));
        }

        public static T GetObject<T>(this IFileStorage2 storage, string path) {
            string json = storage.GetFileContents(path);
            if (String.IsNullOrEmpty(json))
                return default(T);

            return JsonConvert.DeserializeObject<T>(json);
        }

        public static void DeleteFiles(this IFileStorage2 storage, IEnumerable<FileSpec> files) {
            foreach (var file in files)
                storage.DeleteFile(file.Path);
        }

        public static string GetFileContents(this IFileStorage2 storage, string path) {
            using (var stream = storage.GetFileStreamAsync(path).Result)
                return new StreamReader(stream).ReadToEnd();
        }

        public static byte[] GetFileContentsRaw(this IFileStorage2 storage, string path) {
            var stream = storage.GetFileStreamAsync(path).Result;
            return ReadFully(stream);
        }

        private static byte[] ReadFully(Stream input) {
            byte[] buffer = new byte[16 * 1024];
            using (var ms = new MemoryStream()) {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
        }

        public static FileSpec GetFileInfo(this IFileStorage2 storage, string path) {
            return storage.GetFileInfoAsync(path).Result;
        }

        public static bool Exists(this IFileStorage2 storage, string path) {
            return storage.ExistsAsync(path).Result;
        }

        public static bool SaveFile(this IFileStorage2 storage, string path, string contents) {
            return storage.SaveFileAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(contents))).Result;
        }

        public static bool RenameFile(this IFileStorage2 storage, string oldpath, string newpath) {
            return storage.RenameFileAsync(oldpath, newpath).Result;
        }

        public static bool DeleteFile(this IFileStorage2 storage, string path) {
            return storage.DeleteFileAsync(path).Result;
        }

        public static IEnumerable<FileSpec> GetFileList(this IFileStorage2 storage, string searchPattern = null, int? limit = null) {
            return storage.GetFileListAsync(searchPattern, limit).Result;
        }
    }
}
