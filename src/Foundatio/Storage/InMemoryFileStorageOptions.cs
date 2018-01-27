using System;

namespace Foundatio.Storage {
    public class InMemoryFileStorageOptions : FileStorageOptionsBase {
        public long MaxFileSize { get; set; } = 1024 * 1024 * 256;

        public int MaxFiles { get; set; } = 100;
    }

    public static class InMemoryFileStorageOptionsExtensions {
        public static InMemoryFileStorageOptions WithMaxFileSize(this InMemoryFileStorageOptions options, long fileSize) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.MaxFileSize = fileSize;
            return options;
        }

        public static InMemoryFileStorageOptions WithMaxFiles(this InMemoryFileStorageOptions options, int fileCount) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.MaxFiles = fileCount;
            return options;
        }
    }
}
