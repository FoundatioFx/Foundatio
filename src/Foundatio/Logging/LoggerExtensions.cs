using System;
using Foundatio.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Internal;

namespace Foundatio.Logging {
    public static class LoggerExtensions {
        public static ILogger GetLogger(this object target) {
            return target is IHaveLogger accessor ? accessor.Logger : NullLogger.Instance;
        }
    }
}