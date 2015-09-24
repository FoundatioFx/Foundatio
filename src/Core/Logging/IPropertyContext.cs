using System;
using System.Collections.Generic;

namespace Foundatio.Logging
{
    /// <summary>
    /// An <see langword="interface"/> defining a logger property context.
    /// </summary>
    public interface IPropertyContext
    {
        /// <summary>
        /// Applies the context properties to the specified <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The builder to copy the properties to.</param>
        void Apply(ILogBuilder builder);

        /// <summary>
        /// Removes all keys and values from the property context
        /// </summary>
        void Clear();

        /// <summary>
        /// Determines whether the property context contains the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key to locate in the property context.</param>
        /// <returns><c>true</c> if the property context contains an element with the specified <paramref name="key"/>; otherwise, <c>false</c>.</returns>
        bool Contains(string key);

        /// <summary>
        /// Gets the value associated with the specified <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key of the value to get.</param>
        /// <returns>The value associated with the specified <paramref name="key"/>, if the key is found; otherwise <see langword="null"/>.</returns>
        object Get(string key);

        /// <summary>
        /// Gets the keys in the property context.
        /// </summary>
        /// <returns>The keys in the property context.</returns>
        IEnumerable<string> Keys();

        /// <summary>
        /// Removes the value with the specified <paramref name="key" /> from the property context.
        /// </summary>
        /// <param name="key">The key of the element to remove.</param>
        /// <returns><c>true</c> if the element is successfully found and removed; otherwise, <c>false</c>. This method returns <c>false</c> if key is not found.</returns>
        bool Remove(string key);

        /// <summary>
        /// Sets the <paramref name="value"/> associated with the specified <paramref name="key" />.
        /// </summary>
        /// <param name="key">The key of the value to set.</param>
        /// <param name="value">The value associated with the specified key. The value will be converted to a string.</param>
        void Set(string key, object value);

        /// <summary>
        /// Sets the <paramref name="value"/> associated with the specified <paramref name="key" />.
        /// </summary>
        /// <param name="key">The key of the value to set.</param>
        /// <param name="value">The value associated with the specified key. The value will be converted to a string.</param>
        /// <returns>An <see cref="IDisposable"/> that will remove the key on dispose.</returns>
        IDisposable Push(string key, object value);

        /// <summary>
        /// Removes the value with the specified <paramref name="key" /> from the property context.
        /// </summary>
        /// <param name="key">The key of the element to remove.</param>
        /// <returns><c>true</c> if the element is successfully found and removed; otherwise, <c>false</c>. This method returns <c>false</c> if key is not found.</returns>
        object Pop(string key);
    }
}