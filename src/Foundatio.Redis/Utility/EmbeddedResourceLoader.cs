using System.IO;
using System.Reflection;

namespace Foundatio.Redis.Utility {
	internal static class EmbeddedResourceLoader {
		internal static string GetEmbeddedResource(string name) {
			var assembly = typeof(EmbeddedResourceLoader).GetTypeInfo().Assembly;

            using var stream = assembly.GetManifestResourceStream(name);
            using var streamReader = new StreamReader(stream);
            return streamReader.ReadToEnd();
        }
	}
}