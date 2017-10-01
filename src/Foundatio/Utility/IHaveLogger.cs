using Microsoft.Extensions.Logging;

namespace Foundatio.Utility {
    public interface IHaveLogger {
        ILogger Logger { get; }
    }

    public static class LoggerExtensions {
        public static ILogger GetLogger(this object target) {
            return target is IHaveLogger accessor ? accessor.Logger : null;
        }
    }
}
