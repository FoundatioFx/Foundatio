using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility {
    public interface IHaveLogger {
        ILogger Logger { get; }
    }

    public static class LoggerExtensions {
        public static ILogger GetLogger(this object target) {
            return target is IHaveLogger accessor ? accessor.Logger ?? NullLogger.Instance : NullLogger.Instance;
        }
    }
}
