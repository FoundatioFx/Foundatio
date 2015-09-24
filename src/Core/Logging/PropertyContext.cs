using System;
using System.Collections.Generic;

namespace Foundatio.Logging
{
    /// <summary>
    /// A property context that maintains state in a local dictionary
    /// </summary>
    public class PropertyContext : IPropertyContext
    {
        private readonly Dictionary<string, object> _dictionary;

        /// <summary>
        /// Applies the context properties to the specified <paramref name="builder" />.
        /// </summary>
        /// <param name="builder">The builder to copy the properties to.</param>
        public void Apply(ILogBuilder builder)
        {
            foreach (var pair in _dictionary)
                builder.Property(pair.Key, pair.Value);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PropertyContext"/> class.
        /// </summary>
        public PropertyContext()
        {
            _dictionary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Removes all keys and values from the property context
        /// </summary>
        public void Clear()
        {
            _dictionary.Clear();
        }

        /// <summary>
        /// Determines whether the property context contains the specified <paramref name="key" />.
        /// </summary>
        /// <param name="key">The key to locate in the property context.</param>
        /// <returns>
        ///   <c>true</c> if the property context contains an element with the specified <paramref name="key" />; otherwise, <c>false</c>.
        /// </returns>
        public bool Contains(string key)
        {
            return _dictionary.ContainsKey(key);
        }

        /// <summary>
        /// Gets the value associated with the specified <paramref name="key" />.
        /// </summary>
        /// <param name="key">The key of the value to get.</param>
        /// <returns>
        /// The value associated with the specified <paramref name="key" />, if the key is found; otherwise <see langword="null" />.
        /// </returns>
        public object Get(string key)
        {
            object value;
            _dictionary.TryGetValue(key, out value);
            return value;
        }

        /// <summary>
        /// Gets the keys in the property context.
        /// </summary>
        /// <returns>
        /// The keys in the property context.
        /// </returns>
        public IEnumerable<string> Keys()
        {
            return _dictionary.Keys;
        }

        /// <summary>
        /// Removes the value with the specified <paramref name="key" /> from the property context.
        /// </summary>
        /// <param name="key">The key of the element to remove.</param>
        /// <returns>
        ///   <c>true</c> if the element is successfully found and removed; otherwise, <c>false</c>. This method returns <c>false</c> if key is not found.
        /// </returns>
        public bool Remove(string key)
        {
            return _dictionary.Remove(key);
        }

        /// <summary>
        /// Sets the <paramref name="value" /> associated with the specified <paramref name="key" />.
        /// </summary>
        /// <param name="key">The key of the value to set.</param>
        /// <param name="value">The value associated with the specified key.</param>
        /// <returns>
        /// An <see cref="IDisposable" /> that will remove the key on dispose.
        /// </returns>
        public void Set(string key, object value)
        {
            _dictionary[key] = value;
        }

        /// <summary>
        /// Sets the <paramref name="value" /> associated with the specified <paramref name="key" />.
        /// </summary>
        /// <param name="key">The key of the value to set.</param>
        /// <param name="value">The value associated with the specified key.</param>
        /// <returns>
        /// An <see cref="IDisposable" /> that will remove the key on dispose.
        /// </returns>
        public IDisposable Push(string key, object value)
        {
            Set(key, value);
            return new DisposeAction(() => Remove(key));
        }

        /// <summary>
        /// Removes the value with the specified <paramref name="key" /> from the property context.
        /// </summary>
        /// <param name="key">The key of the element to remove.</param>
        /// <returns>
        ///   <c>true</c> if the element is successfully found and removed; otherwise, <c>false</c>. This method returns <c>false</c> if key is not found.
        /// </returns>
        public object Pop(string key)
        {
            var value = Get(key);
            Remove(key);
            return value;
        }
    }
}