using System;

namespace Foundatio.Storage {
    public class AzureFileStorageOptions : SharedOptions {
        public string ConnectionString { get; set; }
        public string ContainerName { get; set; } = "storage";
    }

    public class AzureFileStorageOptionsBuilder : SharedOptionsBuilder<AzureFileStorageOptions, AzureFileStorageOptionsBuilder> {
        public AzureFileStorageOptionsBuilder ConnectionString(string connectionString) {
            Target.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            return this;
        }

        public AzureFileStorageOptionsBuilder ContainerName(string containerName) {
            Target.ContainerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
            return this;
        }
    }
}
