using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Foundatio.Logging {
    /// <summary>
    /// Extension methods for logging
    /// </summary>
    [DebuggerStepThrough]
    public static class LoggerExtensions {
        /// <summary>
        /// Sets the dictionary key to the specified value. 
        /// </summary>
        /// <param name="dictionary">The dictionary to update.</param>
        /// <param name="key">The key to update.</param>
        /// <param name="value">The value to use.</param>
        /// <returns>A dispoable action that removed the key on dispose.</returns>
        public static IDisposable Set(this IDictionary<string, string> dictionary, string key, string value) {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));
            if (key == null)
                throw new ArgumentNullException(nameof(key));

            dictionary[key] = value;

            return new DisposeAction(() => dictionary.Remove(key));
        }

        /// <summary>
        /// Sets the dictionary key to the specified value. 
        /// </summary>
        /// <param name="dictionary">The dictionary to update.</param>
        /// <param name="key">The key to update.</param>
        /// <param name="value">The value to use.</param>
        /// <returns>A dispoable action that removed the key on dispose.</returns>
        public static IDisposable Set(this IDictionary<string, string> dictionary, string key, object value) {
            string v = value != null ? Convert.ToString(value) : null;
            return Set(dictionary, key, v);
        }

        public static string GetFileName(string filePath) {
            var parts = filePath.Split('\\', '/');
            return parts.LastOrDefault();
        }

        public static string GetFileNameWithoutExtension(string path) {
            path = GetFileName(path);
            if (path == null)
                return null;

            int length;
            if ((length = path.LastIndexOf('.')) == -1)
                return path;

            return path.Substring(0, length);
        }
    }
}