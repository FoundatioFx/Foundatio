using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;

namespace Foundatio.Logging
{
    /// <summary>
    /// A property context that maintains state across asynchronous tasks and call contexts.
    /// </summary>
    public class AsynchronousContext : IPropertyContext
    {
        private readonly string _slotName = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Applies the context properties to the specified <paramref name="builder" />.
        /// </summary>
        /// <param name="builder">The builder to copy the properties to.</param>
        public void Apply(ILogBuilder builder)
        {
            var dictionary = GetDictionary();
            if (dictionary == null)
                return;

            foreach (var pair in dictionary)
                builder.Property(pair.Key, pair.Value);
        }

        /// <summary>
        /// Removes all keys and values from the property context
        /// </summary>
        public void Clear()
        {
            CallContext.FreeNamedDataSlot(_slotName);
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
            var dictionary = GetDictionary();
            if (dictionary == null)
                return false;

            return dictionary.ContainsKey(key);
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
            var dictionary = GetDictionary();
            if (dictionary == null)
                return null;

            object value;
            dictionary.TryGetValue(key, out value);

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
            var dictionary = GetDictionary();
            if (dictionary == null)
                return Enumerable.Empty<string>();

            return dictionary.Keys;
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
            var dictionary = GetDictionary();
            if (dictionary == null)
                return false;

            bool removed = dictionary.Remove(key);

            // CallContext value must be immutable, reassign value
            if (dictionary.Count > 0)
                SetDictionary(dictionary);
            else
                Clear();

            return removed;
        }

        /// <summary>
        /// Sets the <paramref name="value" /> associated with the specified <paramref name="key" />.
        /// </summary>
        /// <param name="key">The key of the value to set.</param>
        /// <param name="value">The value associated with the specified key.</param>
        public void Set(string key, object value)
        {
            var dictionary = GetDictionary()
                             ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            dictionary[key] = value;

            // CallContext value must be immutable, reassign value
            SetDictionary(dictionary);
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


        private IDictionary<string, object> GetDictionary()
        {
            var data = CallContext.LogicalGetData(_slotName);
            return data as IDictionary<string, object>;
        }

        private void SetDictionary(IDictionary<string, object> value)
        {
            CallContext.LogicalSetData(_slotName, value);
        }
    }
}