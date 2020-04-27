using System;
using Foundatio.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foundatio.DataProtection {
    public static class DataProtectionBuilderExtensions {
        /// <summary>
        /// Configures the data protection system to persist keys to file storage.
        /// </summary>
        /// <param name="builder">The builder instance to modify.</param>
        /// <param name="storage">The storage account to use.</param>
        /// <param name="loggerFactory">The logger factory to use.</param>
        /// <returns>The value <paramref name="builder"/>.</returns>
        public static IDataProtectionBuilder PersistKeysToFileStorage(this IDataProtectionBuilder builder, IFileStorage storage, ILoggerFactory loggerFactory = null) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            if (storage == null)
                throw new ArgumentNullException(nameof(storage));

            builder.Services.Configure<KeyManagementOptions>(options => {
                options.XmlRepository = new FoundatioStorageXmlRepository(storage, loggerFactory);
            });

            return builder;
        }

        /// <summary>
        /// Configures the data protection system to persist keys to file storage.
        /// </summary>
        /// <param name="builder">The builder instance to modify.</param>
        /// <returns>The value <paramref name="builder"/>.</returns>
        public static IDataProtectionBuilder PersistKeysToFileStorage(this IDataProtectionBuilder builder) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(services => {
                var storage = services.GetService<IFileStorage>();
                var loggerFactory = services.GetService<ILoggerFactory>();

                return new ConfigureOptions<KeyManagementOptions>(options => options.XmlRepository = new FoundatioStorageXmlRepository(storage, loggerFactory));
            });

            return builder;
        }
    }
}