using System;

namespace Foundatio.Storage {
    public class AzureFileStorageOptions : FileStorageOptionsBase {
        public string ConnectionString { get; set; }
        public string ContainerName { get; set; } = "storage";
    }
}