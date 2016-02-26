using System;
using System.Reflection;
using NLog;
using NLog.Config;

namespace Foundatio.Logging.NLog {
    public static class LogFactoryExtensions {
        /// <summary>
        /// Enable NLog as logging provider in Foundatio.
        /// </summary>
        /// <param name="factory"></param>
        /// <returns></returns>
        public static ILoggerFactory AddNLog(this ILoggerFactory factory) {
            //ignore this
            LogManager.AddHiddenAssembly(typeof(LogFactoryExtensions).GetTypeInfo().Assembly);

            factory.AddProvider(new NLogLoggerProvider());

            return factory;
        }

        /// <summary>
        /// Apply NLog configuration from XML config.
        /// </summary>
        /// <param name="fileName">absolute path  NLog configuration file.</param>
        private static void ConfigureNLog(string fileName) {
            LogManager.Configuration = new XmlLoggingConfiguration(fileName, true);
        }
    }
}

