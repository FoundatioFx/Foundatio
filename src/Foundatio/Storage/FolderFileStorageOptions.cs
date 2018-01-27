using System;

namespace Foundatio.Storage {
    public class FolderFileStorageOptions : FileStorageOptionsBase {
        public string Folder { get; set; }
    }

    public static class FolderFileStorageOptionsExtensions {
        public static IOptionsBuilder<FolderFileStorageOptions> Folder(this IOptionsBuilder<FolderFileStorageOptions> builder, string folder) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            if (string.IsNullOrEmpty(folder))
                throw new ArgumentNullException(nameof(folder));
            builder.Target.Folder = folder;
            return builder;
        }
    }
}
