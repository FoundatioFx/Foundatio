using System;
using Foundatio.Utility;

namespace Foundatio.Storage {
    public class InMemoryFileStorageOptions : SharedOptions {
        public long MaxFileSize { get; set; } = 1024 * 1024 * 256;
        public int MaxFiles { get; set; } = 100;
    }

    public class InMemoryFileStorageOptionsBuilder : OptionsBuilder<InMemoryFileStorageOptions>, ISharedOptionsBuilder {
        public InMemoryFileStorageOptionsBuilder MaxFileSize(long maxFileSize) {
            Target.MaxFileSize = maxFileSize;
            return this;
        }

        public InMemoryFileStorageOptionsBuilder MaxFiles(int maxFiles) {
            Target.MaxFiles = maxFiles;
            return this;
        }
    }
}
