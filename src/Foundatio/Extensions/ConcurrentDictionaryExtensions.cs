using System;
using System.Collections.Concurrent;

namespace Foundatio.Extensions {
    internal static class ConcurrentDictionaryExtensions {
        public static bool TryUpdate<TKey, TValue>(this ConcurrentDictionary<TKey,TValue> concurrentDictionary, TKey key, Func<TKey, TValue, TValue> updateValueFactory) {
            if ((object)key == null)
                throw new ArgumentNullException(nameof(key));
            if (updateValueFactory == null)
                throw new ArgumentNullException(nameof(updateValueFactory));
            TValue comparisonValue;
            TValue newValue;
            do {
                if (!concurrentDictionary.TryGetValue(key, out comparisonValue)) {
                    return false;
                }
                newValue = updateValueFactory(key, comparisonValue);
            }
            while (!concurrentDictionary.TryUpdate(key, newValue, comparisonValue));
            return true;
        }
    }
}
