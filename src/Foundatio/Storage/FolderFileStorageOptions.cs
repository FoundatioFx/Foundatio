using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Storage
{
    public class FolderFileStorageOptions
    {
        public string Folder { get; set; }

        public ISerializer Serializer { get; set; }

        public ILoggerFactory LoggerFactory { get; set; }
    }
}
