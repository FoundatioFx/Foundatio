using System;

namespace Foundatio.Caching;

/// <summary>
/// Exception thrown when a cache entry exceeds the configured maximum entry size.
/// </summary>
public class MaxEntrySizeExceededCacheException : CacheException
{
    /// <summary>
    /// Gets the size of the entry that exceeded the limit.
    /// </summary>
    public long EntrySize { get; }

    /// <summary>
    /// Gets the configured maximum entry size.
    /// </summary>
    public long MaxEntrySize { get; }

    /// <summary>
    /// Gets the type name of the entry that exceeded the limit.
    /// </summary>
    public string EntryType { get; }

    /// <summary>
    /// Creates a new instance of <see cref="MaxEntrySizeExceededCacheException"/>.
    /// </summary>
    /// <param name="entrySize">The size of the entry that exceeded the limit.</param>
    /// <param name="maxEntrySize">The configured maximum entry size.</param>
    /// <param name="entryType">The type name of the entry.</param>
    public MaxEntrySizeExceededCacheException(long entrySize, long maxEntrySize, string entryType)
        : base($"Cache entry size {entrySize:N0} bytes exceeds maximum allowed size {maxEntrySize:N0} bytes for type {entryType}")
    {
        EntrySize = entrySize;
        MaxEntrySize = maxEntrySize;
        EntryType = entryType;
    }

    /// <summary>
    /// Creates a new instance of <see cref="MaxEntrySizeExceededCacheException"/> with a custom message.
    /// </summary>
    /// <remarks>
    /// This overload is intended for advanced scenarios where entry size metadata is unavailable,
    /// such as deserialization or wrapping other exceptions. When using this constructor,
    /// <see cref="EntrySize"/>, <see cref="MaxEntrySize"/>, and <see cref="EntryType"/> will be 0/null.
    /// </remarks>
    /// <param name="message">The error message.</param>
    public MaxEntrySizeExceededCacheException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="MaxEntrySizeExceededCacheException"/> with a custom message and inner exception.
    /// </summary>
    /// <remarks>
    /// This overload is intended for advanced scenarios where entry size metadata is unavailable,
    /// such as deserialization or wrapping other exceptions. When using this constructor,
    /// <see cref="EntrySize"/>, <see cref="MaxEntrySize"/>, and <see cref="EntryType"/> will be 0/null.
    /// </remarks>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public MaxEntrySizeExceededCacheException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

