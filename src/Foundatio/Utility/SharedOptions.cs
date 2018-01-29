using System;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio {
    public class SharedOptions {
        public ISerializer Serializer { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public interface ISharedOptionsBuilder : IOptionsBuilder {}

    public static class SharedOptionsBuilderExtensions {
        public static T Serializer<T>(this T builder, ISerializer serializer) where T: ISharedOptionsBuilder {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target<SharedOptions>().Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));;
            return (T)builder;
        }

        public static T LoggerFactory<T>(this T builder, ILoggerFactory loggerFactory) where T: ISharedOptionsBuilder {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target<SharedOptions>().LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));;
            return (T)builder;
        }
    }
}
