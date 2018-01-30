using System;
using Foundatio.Utility;

namespace Foundatio.Storage {
    public class FolderFileStorageOptions : SharedOptions {
        public string Folder { get; set; }
    }

    public class FolderFileStorageOptionsBuilder : SharedOptionsBuilder<FolderFileStorageOptions, FolderFileStorageOptionsBuilder> {
        public FolderFileStorageOptionsBuilder Folder(string folder) {
            if (string.IsNullOrEmpty(folder))
                throw new ArgumentNullException(nameof(folder));
            Target.Folder = folder;
            return this;
        }
    }
}
