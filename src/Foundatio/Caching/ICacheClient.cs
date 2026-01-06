using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Foundatio.Caching;

/// <summary>
/// Provides a unified interface for cache operations across different cache implementations.
/// All implementations should behave consistently for the same operations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Expiration Behavior:</b> The <c>expiresIn</c> parameter controls cache entry expiration:
/// </para>
/// <list type="bullet">
///   <item><description><b>null</b>: The entry will not expire (or uses implementation-specific default behavior).
///   When called on an existing key, passing null removes any existing expiration.</description></item>
///   <item><description><b>Positive value</b>: The entry will expire after the specified duration from now.</description></item>
///   <item><description><b>Zero or negative value</b>: The operation is treated as expired - any existing key is removed
///   and the method returns a failure/default value (false, 0, depending on the method).</description></item>
///   <item><description><b>TimeSpan.MaxValue</b>: The entry will not expire (treated as no expiration).</description></item>
/// </list>
/// <para>
/// <b>Note on IncrementAsync:</b> Unlike other operations, <see cref="IncrementAsync(string, double, TimeSpan?)"/>
/// preserves any existing expiration when <c>expiresIn</c> is null. This is because increment is an in-place
/// modification rather than a value replacement, making TTL preservation the expected behavior for use cases
/// like rate limiting counters.
/// </para>
/// </remarks>
public interface ICacheClient : IDisposable
{
    /// <summary>
    /// Removes a cache entry by its key.
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <returns>
    /// <c>true</c> if the key was found and removed; <c>false</c> if the key did not exist or was already expired.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<bool> RemoveAsync(string key);

    /// <summary>
    /// Removes a cache entry only if its current value equals the expected value.
    /// This operation is atomic and can be used for optimistic concurrency control.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="expected">The expected value that must match the current cached value for removal to succeed.</param>
    /// <returns>
    /// <c>true</c> if the key existed, the value matched, and the entry was removed;
    /// <c>false</c> if the key did not exist or the value did not match.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<bool> RemoveIfEqualAsync<T>(string key, T expected);

    /// <summary>
    /// Removes multiple cache entries, or all entries if no keys are specified.
    /// </summary>
    /// <param name="keys">
    /// The keys to remove. If null or empty, all cache entries will be removed (flush).
    /// Each key in the collection cannot be null or empty.
    /// </param>
    /// <returns>The number of entries that were removed.</returns>
    /// <exception cref="ArgumentException">Thrown when any key in <paramref name="keys"/> is null or empty.</exception>
    Task<int> RemoveAllAsync(IEnumerable<string> keys = null);

    /// <summary>
    /// Removes all cache entries whose keys start with the specified prefix.
    /// </summary>
    /// <param name="prefix">
    /// The prefix to match. If null or empty, all cache entries will be removed (flush).
    /// </param>
    /// <returns>The number of entries that were removed.</returns>
    Task<int> RemoveByPrefixAsync(string prefix);

    /// <summary>
    /// Retrieves a cache entry by its key.
    /// </summary>
    /// <typeparam name="T">The expected type of the cached value.</typeparam>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <returns>
    /// A <see cref="CacheValue{T}"/> containing the cached value if found.
    /// Use <see cref="CacheValue{T}.HasValue"/> to check if a value was found,
    /// and <see cref="CacheValue{T}.IsNull"/> to check if the cached value is explicitly null.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<CacheValue<T>> GetAsync<T>(string key);

    /// <summary>
    /// Retrieves multiple cache entries by their keys.
    /// </summary>
    /// <typeparam name="T">The expected type of the cached values.</typeparam>
    /// <param name="keys">
    /// The keys to retrieve. Cannot be null. Each key in the collection cannot be null or empty.
    /// Duplicate keys are automatically deduplicated.
    /// </param>
    /// <returns>
    /// A dictionary mapping each requested key to its <see cref="CacheValue{T}"/>.
    /// All requested keys will be present in the result, with <see cref="CacheValue{T}.HasValue"/>
    /// indicating whether the key was found.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="keys"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any key in <paramref name="keys"/> is null or empty.</exception>
    Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys);

    /// <summary>
    /// Adds a cache entry only if the key does not already exist.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache.</typeparam>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Entry will not expire.</description></item>
    ///   <item><description><b>Positive value</b>: Entry expires after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: Operation fails, any existing key is removed, returns false.</description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// <c>true</c> if the key was added (did not exist); <c>false</c> if the key already exists or expiration was invalid.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null);

    /// <summary>
    /// Sets a cache entry, creating it if it doesn't exist or overwriting if it does.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache.</typeparam>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Entry will not expire.</description></item>
    ///   <item><description><b>Positive value</b>: Entry expires after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns false.</description></item>
    /// </list>
    /// </param>
    /// <returns><c>true</c> if the value was set; <c>false</c> if the expiration was invalid.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null);

    /// <summary>
    /// Sets multiple cache entries at once.
    /// </summary>
    /// <typeparam name="T">The type of the values to cache.</typeparam>
    /// <param name="values">
    /// A dictionary of key-value pairs to cache. Cannot be null. Each key cannot be null or empty.
    /// </param>
    /// <param name="expiresIn">
    /// Optional expiration time for all cache entries.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Entries will not expire.</description></item>
    ///   <item><description><b>Positive value</b>: Entries expire after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: All specified keys are removed, returns 0.</description></item>
    /// </list>
    /// </param>
    /// <returns>The number of entries that were successfully set.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any key in <paramref name="values"/> is null or empty.</exception>
    Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null);

    /// <summary>
    /// Replaces an existing cache entry's value. Only succeeds if the key already exists.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache.</typeparam>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="value">The new value to cache.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Entry will not expire (removes any existing expiration).</description></item>
    ///   <item><description><b>Positive value</b>: Entry expires after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns false.</description></item>
    /// </list>
    /// </param>
    /// <returns><c>true</c> if the key existed and was replaced; <c>false</c> if the key did not exist or expiration was invalid.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null);

    /// <summary>
    /// Replaces a cache entry only if its current value equals the expected value.
    /// This operation is atomic and can be used for optimistic concurrency control.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="value">The new value to cache.</param>
    /// <param name="expected">The expected current value that must match for the replace to succeed.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Entry will not expire (removes any existing expiration).</description></item>
    ///   <item><description><b>Positive value</b>: Entry expires after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns false.</description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// <c>true</c> if the key existed, the value matched, and was replaced;
    /// <c>false</c> if the key did not exist, the value did not match, or expiration was invalid.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null);

    /// <summary>
    /// Atomically increments a floating-point value stored at the specified key.
    /// If the key does not exist, it is initialized to <paramref name="amount"/>.
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="amount">
    /// The amount to increment by. Can be positive, negative (decrement), or zero.
    /// Supports fractional values (e.g., 1.5, -0.25).
    /// </param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Preserves any existing expiration. For new keys, no expiration is set.</description></item>
    ///   <item><description><b>Positive value</b>: Sets or updates expiration to this duration from now.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns 0.</description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// The new value after incrementing. Returns 0 if the operation failed due to invalid expiration.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null);

    /// <summary>
    /// Atomically increments an integer value stored at the specified key.
    /// If the key does not exist, it is initialized to <paramref name="amount"/>.
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="amount">
    /// The amount to increment by. Can be positive, negative (decrement), or zero.
    /// </param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Preserves any existing expiration. For new keys, no expiration is set.</description></item>
    ///   <item><description><b>Positive value</b>: Sets or updates expiration to this duration from now.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns 0.</description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// The new value after incrementing. Returns 0 if the operation failed due to invalid expiration.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null);

    /// <summary>
    /// Checks whether a cache entry exists for the specified key.
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <returns><c>true</c> if the key exists and has not expired; <c>false</c> otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<bool> ExistsAsync(string key);

    /// <summary>
    /// Gets the remaining time-to-live (TTL) for a cache entry.
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <returns>
    /// The remaining time until expiration, or <c>null</c> if the key does not exist or has no expiration set.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<TimeSpan?> GetExpirationAsync(string key);

    /// <summary>
    /// Gets the remaining time-to-live (TTL) for multiple cache entries.
    /// </summary>
    /// <param name="keys">
    /// The keys to check. Cannot be null. Each key cannot be null or empty.
    /// Duplicate keys are automatically deduplicated.
    /// </param>
    /// <returns>
    /// A dictionary mapping each existing key to its remaining TTL.
    /// Keys that exist but have no expiration will have a <c>null</c> value.
    /// Keys that don't exist or are expired are omitted from the result.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="keys"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any key in <paramref name="keys"/> is null or empty.</exception>
    Task<IDictionary<string, TimeSpan?>> GetAllExpirationAsync(IEnumerable<string> keys);

    /// <summary>
    /// Sets the expiration time for an existing cache entry.
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="expiresIn">The duration until the entry expires. Must be positive.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task SetExpirationAsync(string key, TimeSpan expiresIn);

    /// <summary>
    /// Sets expiration times for multiple cache entries.
    /// </summary>
    /// <param name="expirations">
    /// A dictionary mapping keys to their new expiration times. Cannot be null.
    /// Each key cannot be null or empty. A <c>null</c> expiration value removes the expiration for that key.
    /// </param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="expirations"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any key in <paramref name="expirations"/> is null or empty.</exception>
    Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations);

    /// <summary>
    /// Atomically sets the value if it is higher than the current value.
    /// If the key does not exist, it is created with the specified value.
    /// Useful for tracking maximum values (e.g., high water marks).
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="value">The value to compare and potentially set. Supports fractional values.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Entry will not expire (removes any existing expiration).</description></item>
    ///   <item><description><b>Positive value</b>: Entry expires after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns 0.</description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// The difference between the new value and the old value if the value was updated;
    /// 0 if the current value was already higher or equal or invalid expiration.
    /// For new keys, returns the value itself (difference from 0).
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null);

    /// <summary>
    /// Atomically sets the value if it is higher than the current value.
    /// If the key does not exist, it is created with the specified value.
    /// Useful for tracking maximum values (e.g., high water marks).
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="value">The value to compare and potentially set.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Entry will not expire (removes any existing expiration).</description></item>
    ///   <item><description><b>Positive value</b>: Entry expires after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns 0.</description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// The difference between the new value and the old value if the value was updated;
    /// 0 if the current value was already higher or equal or invalid expiration.
    /// For new keys, returns the value itself (difference from 0).
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null);

    /// <summary>
    /// Atomically sets the value if it is lower than the current value.
    /// If the key does not exist, it is created with the specified value.
    /// Useful for tracking minimum values (e.g., fastest response time).
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="value">The value to compare and potentially set. Supports fractional values.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Entry will not expire (removes any existing expiration).</description></item>
    ///   <item><description><b>Positive value</b>: Entry expires after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns 0.</description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// The difference between the old value and the new value if the value was updated;
    /// 0 if the current value was already lower or equal or invalid expiration.
    /// For new keys, returns the value itself (difference from 0).
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null);

    /// <summary>
    /// Atomically sets the value if it is lower than the current value.
    /// If the key does not exist, it is created with the specified value.
    /// Useful for tracking minimum values (e.g., fastest response time).
    /// </summary>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="value">The value to compare and potentially set.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry.
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Entry will not expire (removes any existing expiration).</description></item>
    ///   <item><description><b>Positive value</b>: Entry expires after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns 0.</description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// The difference between the old value and the new value if the value was updated;
    /// 0 if the current value was already lower or equal or invalid expiration.
    /// For new keys, returns the value itself (difference from 0).
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null);

    /// <summary>
    /// Adds values to a list stored at the specified key.
    /// If the key does not exist, a new list is created.
    /// </summary>
    /// <typeparam name="T">The type of values in the list.</typeparam>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="values">The values to add to the list. Cannot be null. Null values within the collection are ignored.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for each value being added (per-value expiration, NOT per-key).
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Values will not expire.</description></item>
    ///   <item><description><b>Positive value</b>: Each value expires independently after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: The specified values are removed from the list if present, returns 0.</description></item>
    /// </list>
    /// <para>
    /// <b>Design Note:</b> Per-value expiration prevents unbounded list growth. Without it, adding any item
    /// would reset the entire list's TTL (sliding expiration), causing lists to grow indefinitely in
    /// scenarios like tracking deleted items or recent activity.
    /// </para>
    /// </param>
    /// <returns>The number of values that were added to the list.</returns>
    /// <remarks>
    /// The key's overall expiration is automatically set to the maximum expiration of all items in the list.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> or <paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null);

    /// <summary>
    /// Removes values from a list stored at the specified key.
    /// </summary>
    /// <typeparam name="T">The type of values in the list.</typeparam>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="values">The values to remove from the list. Cannot be null. Null values within the collection are ignored.</param>
    /// <param name="expiresIn">
    /// Optional expiration time for the cache entry (applied after the remove operation).
    /// <list type="bullet">
    ///   <item><description><b>null</b>: Expiration is not modified.</description></item>
    ///   <item><description><b>Positive value</b>: Entry expires after this duration.</description></item>
    ///   <item><description><b>Zero or negative</b>: Any existing key is removed, returns 0.</description></item>
    /// </list>
    /// </param>
    /// <returns>The number of values that were removed from the list.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> or <paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null);

    /// <summary>
    /// Retrieves values from a list stored at the specified key.
    /// </summary>
    /// <typeparam name="T">The type of values in the list.</typeparam>
    /// <param name="key">The unique identifier for the cache entry. Cannot be null or empty.</param>
    /// <param name="page">
    /// Optional 1-based page number for pagination. If null, returns all values.
    /// Must be 1 or greater when specified.
    /// </param>
    /// <param name="pageSize">The number of items per page when paginating. Defaults to 100.</param>
    /// <returns>
    /// A <see cref="CacheValue{T}"/> containing the list values if found.
    /// Use <see cref="CacheValue{T}.HasValue"/> to check if the list exists.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="page"/> is less than 1.</exception>
    Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100);
}
