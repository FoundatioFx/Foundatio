using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Foundatio.Storage {
    public interface IFileStorage : IDisposable {
        string GetFileContents(string path);
        FileInfo GetFileInfo(string path);
        bool Exists(string path);
        bool SaveFile(string path, string contents);
        bool RenameFile(string oldpath, string newpath);
        bool DeleteFile(string path);
        IEnumerable<FileInfo> GetFileList(string searchPattern = null, int? limit = null);
    }

    public class FileInfo {
        public string Path { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public long Size { get; set; }
    }

    public static class FileStorageExtensions {
        public static bool SaveObject<T>(this IFileStorage storage, string path, T data) {
            return storage.SaveFile(path, JsonConvert.SerializeObject(data));
        }

        public static T GetObject<T>(this IFileStorage storage, string path) {
            string json = storage.GetFileContents(path);
            return JsonConvert.DeserializeObject<T>(json);
        }

        public static void DeleteFiles(this IFileStorage storage, IEnumerable<FileInfo> files) {
            foreach (var file in files)
                storage.DeleteFile(file.Path);
        }
    }
}
