namespace Foundatio.Storage {
    public class InMemoryFileStorageOptions : FileStorageOptionsBase {
        public long MaxFileSize { get; set; } = 1024 * 1024 * 256;

        public int MaxFiles { get; set; } = 100;
    }
}
