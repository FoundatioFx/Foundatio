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
        /// Enable NLog as logging provider in Foundatio.
        /// </summary>
        /// <param name="factory"></param>
        /// <param name="populateAdditionalLogEventInfo">Callback that allows populating additional log event information from the log state and scopes.</param>
        /// <returns></returns>
        public static ILoggerFactory AddNLog(this ILoggerFactory factory, Action<object, object[], LogEventInfo> populateAdditionalLogEventInfo) {
            //ignore this
            LogManager.AddHiddenAssembly(typeof(LogFactoryExtensions).GetTypeInfo().Assembly);

            factory.AddProvider(new NLogLoggerProvider(populateAdditionalLogEventInfo));

            return factory;
        }

        /// <summary>
        /// Apply NLog configuration from XML config.
        /// </summary>
        /// <param name="fileName">absolute path  NLog configuration file.</param>
        public static void ConfigureNLog(string fileName) {
            LogManager.Configuration = new XmlLoggingConfiguration(fileName, true);
        }
    }
}

