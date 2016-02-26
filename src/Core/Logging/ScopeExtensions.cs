using System;
using Foundatio.Logging.Internal;

namespace Foundatio.Logging {
    public static class ScopeExtensions {
        /// <summary>
        /// Formats the message and creates a scope.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to create the scope in.</param>
        /// <param name="messageFormat">Format string of the scope message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        /// <returns>A disposable scope object. Can be null.</returns>
        public static IDisposable BeginScope(this ILogger logger, string messageFormat, params object[] args) {
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            if (messageFormat == null)
                throw new ArgumentNullException(nameof(messageFormat));

            return logger.BeginScope(s => s.ToString(), new FormattedLogValues(messageFormat, args));
        }

        /// <summary>
        /// Creates a scoped log using a builder.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to create the scope in.</param>
        /// <param name="builder">The scope builder used to build the scope.</param>
        /// <returns>A disposable scope object. Can be null.</returns>
        public static IDisposable BeginScope(this ILogger logger, Action<PropertyBuilder> builder) {
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            var propertyBuilder = new PropertyBuilder();
            builder(propertyBuilder);

            return logger.BeginScope(s => s, propertyBuilder.Properties);
        }
    }
}
