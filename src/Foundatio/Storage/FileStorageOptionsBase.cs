using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Storage {
    public abstract class FileStorageOptionsBase {
        public ISerializer Serializer { get; set; }

        public ILoggerFactory LoggerFactory { get; set; }
    }
}
