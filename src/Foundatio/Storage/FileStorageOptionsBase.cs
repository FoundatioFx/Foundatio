using System;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Storage {
    public abstract class FileStorageOptionsBase {
        public ISerializer Serializer { get; set; }

        public ILoggerFactory LoggerFactory { get; set; }
    }

    public static class FileStorageOptionsExtensions {
        public static IOptionsBuilder<FileStorageOptionsBase> Serializer(this IOptionsBuilder<FileStorageOptionsBase> builder, ISerializer serializer) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return builder;
        }

        public static IOptionsBuilder<FileStorageOptionsBase> LoggerFactory(this IOptionsBuilder<FileStorageOptionsBase> builder, ILoggerFactory loggerFactory) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return builder;
        }
    }
}
