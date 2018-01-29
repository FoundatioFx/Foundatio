using System;

namespace Foundatio.Storage {
    public class AzureFileStorageOptions : FileStorageOptionsBase {
        public string ConnectionString { get; set; }
        public string ContainerName { get; set; } = "storage";
    }

    public static class FileStorageOptionsExtensions {
        public static IOptionsBuilder<AzureFileStorageOptions> ConnectionString(this IOptionsBuilder<AzureFileStorageOptions> builder, string connectionString) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            
            builder.Target.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            return builder;
        }

        public static IOptionsBuilder<AzureFileStorageOptions> ContainerName(this IOptionsBuilder<AzureFileStorageOptions> builder, string containerName) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            builder.Target.ContainerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
            return builder;
        }
    }
}
