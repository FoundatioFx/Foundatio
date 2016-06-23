using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Newtonsoft.Json;

namespace Foundatio.Storage {
    public interface IFileStorage : IDisposable {
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
        public static Task<bool> SaveObjectAsync<T>(this IFileStorage storage, string path, T data, CancellationToken cancellationToken = default(CancellationToken)) {
            string json = JsonConvert.SerializeObject(data);
            return storage.SaveFileAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(json ?? String.Empty)), cancellationToken);
        }
        
        public static async Task<T> GetObjectAsync<T>(this IFileStorage storage, string path, CancellationToken cancellationToken = default(CancellationToken)) {
            string fileContents = null;
            using (Stream stream = await storage.GetFileStreamAsync(path, cancellationToken).AnyContext()) {
                if (stream != null)
                    fileContents = await new StreamReader(stream).ReadToEndAsync().AnyContext();
            }

            if (String.IsNullOrEmpty(fileContents))
                return default(T);

            return JsonConvert.DeserializeObject<T>(fileContents);
        }

        public static async Task DeleteFilesAsync(this IFileStorage storage, IEnumerable<FileSpec> files) {
            foreach (var file in files)
                await storage.DeleteFileAsync(file.Path).AnyContext();
        }

        public static async Task<string> GetFileContentsAsync(this IFileStorage storage, string path) {
            using (var stream = await storage.GetFileStreamAsync(path).AnyContext()) {
                if (stream != null)
                    return await new StreamReader(stream).ReadToEndAsync().AnyContext();
            }

            return null;
        }

        public static async Task<byte[]> GetFileContentsRawAsync(this IFileStorage storage, string path) {
            using (var stream = await storage.GetFileStreamAsync(path).AnyContext()) {
                if (stream == null)
                    return null;
                
                byte[] buffer = new byte[16 * 1024];
                using (var ms = new MemoryStream()) {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).AnyContext()) > 0) {
                        ms.Write(buffer, 0, read);
                    }

                    return ms.ToArray();
                }
            }
        }

        public static Task<bool> SaveFileAsync(this IFileStorage storage, string path, string contents) {
            return storage.SaveFileAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(contents ?? String.Empty)));
        }
    }
}
