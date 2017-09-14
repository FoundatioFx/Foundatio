using Microsoft.Extensions.Logging;

namespace Foundatio.Caching {
    public abstract class CacheClientOptionsBase {
        public ILoggerFactory LoggerFactory { get; set; }
    }
}