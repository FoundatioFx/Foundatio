using System;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Storage {
    public abstract class FileStorageOptionsBase {
        public ISerializer Serializer { get; set; }

        public ILoggerFactory LoggerFactory { get; set; }
    }

    public static class FileStorageOptionsExtensions {
        public static FileStorageOptionsBase WithSerializer(this FileStorageOptionsBase options, ISerializer serializer) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return options;
        }

        public static FileStorageOptionsBase WithLoggerFactory(this FileStorageOptionsBase options, ILoggerFactory loggerFactory) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return options;
        }
    }
}
