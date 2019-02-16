using Serilog.Extensions.Logging;
using IExtensionsLogger = Microsoft.Extensions.Logging.ILogger;

namespace Foundatio.HostingSample {
    public static class SerilogExtensions {
        public static IExtensionsLogger ToExtensionsLogger(this Serilog.ILogger logger) {
            return new SerilogLoggerProvider(logger).CreateLogger(nameof(Program));
        }
    }
}
