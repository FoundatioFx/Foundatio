using System;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Storage {
    public abstract class FileStorageOptionsBase {
        public ISerializer Serializer { get; set; }

        public ILoggerFactory LoggerFactory { get; set; }
    }

    public static class FileStorageOptionsExtensions {
        public static IOptionsBuilder<T> Serializer<T>(this IOptionsBuilder<T> builder, ISerializer serializer) where T : FileStorageOptionsBase {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return builder;
        }

        public static IOptionsBuilder<T> LoggerFactory<T>(this IOptionsBuilder<T> builder, ILoggerFactory loggerFactory) where T : FileStorageOptionsBase {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return builder;
        }
    }
}
