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

    public MaxEntrySizeExceededCacheException(string message) : base(message)
    {
    }

    public MaxEntrySizeExceededCacheException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

