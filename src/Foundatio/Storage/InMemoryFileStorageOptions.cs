using System;

namespace Foundatio.Storage {
    public class InMemoryFileStorageOptions : FileStorageOptionsBase {
        public long MaxFileSize { get; set; } = 1024 * 1024 * 256;

        public int MaxFiles { get; set; } = 100;
    }

    public static class InMemoryFileStorageOptionsExtensions {
        public static IOptionsBuilder<InMemoryFileStorageOptions> MaxFileSize(this IOptionsBuilder<InMemoryFileStorageOptions> builder, long fileSize) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.MaxFileSize = fileSize;
            return builder;
        }

        public static IOptionsBuilder<InMemoryFileStorageOptions> MaxFileSize(this IOptionsBuilder<InMemoryFileStorageOptions> builder, int fileCount) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.MaxFiles = fileCount;
            return builder;
        }
    }
}
