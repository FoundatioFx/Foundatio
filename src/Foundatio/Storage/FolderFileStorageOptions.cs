using System;

namespace Foundatio.Storage {
    public class FolderFileStorageOptions : FileStorageOptionsBase {
        public string Folder { get; set; }
    }

    public static class FolderFileStorageOptionsExtensions {
        public static FolderFileStorageOptions WithFolder(this FolderFileStorageOptions options, string folder) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(folder))
                throw new ArgumentNullException(nameof(folder));
            options.Folder = folder;
            return options;
        }
    }
}
