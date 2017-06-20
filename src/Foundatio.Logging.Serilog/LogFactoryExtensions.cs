using System;

namespace Foundatio.Logging.Serilog {
    public static class LogFactoryExtensions {
        /// <summary>
        /// Add Serilog to the logging pipeline.
        /// </summary>
        /// <param name="factory">The logger factory to configure.</param>
        /// <param name="logger">The Serilog logger; if not supplied, the static Serilog.Log will be used.</param>
        /// <returns>The logger factory.</returns>
        public static ILoggerFactory AddSerilog(this ILoggerFactory factory, global::Serilog.ILogger logger) {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            return factory.AddSerilog(logger, false);
        }

        /// <summary>
        /// Add Serilog to the logging pipeline.
        /// </summary>
        /// <param name="factory">The logger factory to configure.</param>
        /// <param name="logger">The Serilog logger; if not supplied, the static Serilog.Log will be used.</param>
        /// <param name="dispose">When true, dispose <paramref name="logger"/> when the framework disposes the provider. If the
        /// logger is not specified but <paramref name="dispose"/> is true, the Log.CloseAndFlush() method will be
        /// called on the static Log class instead.</param>
        /// <returns>The logger factory.</returns>
        public static ILoggerFactory AddSerilog(this ILoggerFactory factory, global::Serilog.ILogger logger = null, bool dispose = false) {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            factory.AddProvider(new SerilogLoggerProvider(logger, dispose));

            return factory;
        }
    }
}