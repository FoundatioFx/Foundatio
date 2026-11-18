using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Foundatio.Caching;

/// <summary>
/// A list value as the cache stores it, so <see cref="InMemoryCacheClient.CacheEntry"/> can read it without knowing the member type.
/// </summary>
internal interface ICacheList
{
    /// <summary>The type of the dictionary the list is read as.</summary>
    Type DictionaryType { get; }

    /// <summary>
    /// Returns the dictionary to hand to a caller. Without cloning, callers have always received the cached
    /// dictionary itself, so it is returned as is and the next list update moves to a private copy first.
    /// </summary>
    object Expose();

    /// <summary>Returns a private copy of the dictionary for <see cref="InMemoryCacheClientOptions.CloneValues"/>.</summary>
    object Clone(bool forceDictionaryRebuild);
}

/// <summary>
/// The members of a list and their expirations. Writers take turns on <see cref="SyncRoot"/> and change the
/// dictionary in place, so an update costs only the members it touches. Changes are journaled until
/// <see cref="Commit"/> so a rejected or lost update can be rolled back.
/// </summary>
/// <remarks>
/// Once the dictionary has been handed out by <see cref="Expose"/> it is never changed again: the next update
/// copies it, which is the same work every update did before lists were updated in place. Lists that are only
/// read with <c>GetListAsync</c> never pay for a copy.
/// </remarks>
internal sealed class CacheList<T> : ICacheList where T : notnull
{
    private readonly List<(T Key, bool Existed, DateTime? Expiration)> _journal = [];
    private IDictionary<T, DateTime?> _values;
    private IDictionary<T, DateTime?>? _journaledValues;
    private Bounds _bounds;
    private Bounds _journaledBounds;
    private bool _journaledExpired;
    private volatile IDictionary<T, DateTime?>? _exposedValues;
    private readonly bool _hasCallbackFreeComparer;
    private bool _applying;

    private CacheList(IDictionary<T, DateTime?> values, bool exposed)
    {
        _values = values;
        _exposedValues = exposed ? values : null;
        _bounds = Bounds.Calculate(values);
        _hasCallbackFreeComparer = _hasValueEquality
            && values.GetType() == typeof(Dictionary<T, DateTime?>)
            && ReferenceEquals(((Dictionary<T, DateTime?>)values).Comparer, EqualityComparer<T>.Default);
    }

    /// <summary>
    /// Whether default equality for <typeparamref name="T"/> runs no caller code and treats only identical values as
    /// equal, so a rollback can restore the member it removed from the key it was given. Custom structs, and framework
    /// types such as <see cref="DateTime"/> or <see cref="decimal"/> whose equal values can differ, are excluded.
    /// </summary>
    private static readonly bool _hasValueEquality = typeof(T) == typeof(string) || typeof(T) == typeof(Guid)
        || typeof(T) == typeof(TimeSpan) || typeof(T).IsEnum
        || typeof(T).IsPrimitive && typeof(T) != typeof(float) && typeof(T) != typeof(double);

    /// <summary>
    /// Whether an update can change the members in place and undo itself from the journal. That needs a comparer
    /// that runs no caller code, which could throw partway through or treat distinct keys as equal, and no other
    /// update in progress: a size calculator can call back into the cache from inside an update.
    /// </summary>
    public bool CanUpdateInPlace => _hasCallbackFreeComparer && !_applying;

    /// <summary>
    /// Returns a private list holding the committed members, for an update that cannot run in place. A change
    /// still in progress on this list is left out, so a nested update never stores or undoes it.
    /// </summary>
    public CacheList<T> CreateCandidate()
    {
        if (!_applying)
            return new CacheList<T>(CopyDictionary(_values), exposed: false);

        if (_journaledValues is not null)
            return new CacheList<T>(CopyDictionary(_journaledValues), exposed: false);

        var values = CopyDictionary(_values);
        for (int index = _journal.Count - 1; index >= 0; index--)
        {
            var (key, existed, expiration) = _journal[index];
            if (existed)
                values[key] = expiration;
            else
                values.Remove(key);
        }

        return new CacheList<T>(values, exposed: false);
    }

    public System.Threading.Lock SyncRoot { get; } = new();

    /// <summary>The live members. Only read them, or pass them to callbacks, while holding <see cref="SyncRoot"/>.</summary>
    public IDictionary<T, DateTime?> Values => _values;

    public int Count => _values.Count;

    /// <summary>When the last member expires, or <c>null</c> while any member never expires.</summary>
    public DateTime? ExpiresAt => _bounds.PermanentCount > 0 ? null : _bounds.Latest;

    public Type DictionaryType => _values.GetType();

    /// <summary>
    /// Adopts a dictionary stored with <c>SetAsync</c>. The caller may still hold it, so the first update copies it.
    /// </summary>
    public static CacheList<T> Adopt(IDictionary<T, DateTime?> values) => new(values, exposed: true);

    /// <summary>Starts a list from a dictionary nothing else holds.</summary>
    public static CacheList<T> Create(Dictionary<T, DateTime?> values) => new(values, exposed: false);

    public static CacheList<T> Create(HashSet<T> items, DateTime? expiration)
    {
        var values = new Dictionary<T, DateTime?>(items.Count, items.Comparer);
        foreach (var item in items)
            values[item] = expiration;
        return Create(values);
    }

    /// <summary>
    /// Hands out the members as a dictionary. It stays shared until the next update copies it, so readers that
    /// find it already shared return it without taking the lock.
    /// </summary>
    public object Expose()
    {
        if (_exposedValues is { } exposed)
            return exposed;

        lock (SyncRoot)
        {
            _exposedValues = _values;
            return _values;
        }
    }

    private bool IsExposed => _exposedValues is not null;

    public object Clone(bool forceDictionaryRebuild)
    {
        lock (SyncRoot)
        {
            // Expirations are immutable, so a dictionary of immutable keys only needs its entries copied. The copy
            // constructor never carries over cached Keys or Values views, which a deep clone would leave bound to
            // the live dictionary.
            if (!forceDictionaryRebuild && _values.GetType() == typeof(Dictionary<T, DateTime?>)
                && FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(T)))
            {
                var dictionary = (Dictionary<T, DateTime?>)_values;
                if (ReferenceEquals(dictionary.Comparer, EqualityComparer<T>.Default)
                    || FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(dictionary.Comparer.GetType()))
                    return new Dictionary<T, DateTime?>(dictionary, dictionary.Comparer);
            }

            return InMemoryCacheClient.CacheEntry.CloneValue(_values, forceDictionaryRebuild)!;
        }
    }

    /// <summary>
    /// Adds or removes <paramref name="items"/>, first dropping expired members when any may have expired. Returns
    /// the number of items added, or removed, sets <paramref name="changed"/> when the members changed and
    /// <paramref name="expired"/> to the number of expired members dropped. Call with <see cref="SyncRoot"/> held,
    /// then <see cref="Commit"/> or <see cref="Rollback"/>.
    /// </summary>
    public long Apply(HashSet<T> items, DateTime? expiration, DateTime utcNow, bool remove, out bool changed, out int expired)
    {
        _applying = true;
        _journaledBounds = _bounds;
        expired = IsExposed || _bounds.Earliest < utcNow ? RemoveExpired(utcNow) : 0;
        _journaledExpired = expired > 0;
        changed = _journaledExpired;

        long count = 0;
        bool recalculateBounds = false;
        foreach (var key in items)
        {
            bool existed = _values.TryGetValue(key, out var previous);
            if (remove)
            {
                if (!existed)
                    continue;

                PrepareForChange();
                _values.Remove(key);
                count++;
            }
            else
            {
                count++;
                if (existed && IsSameExpiration(previous, expiration))
                    continue;

                PrepareForChange();
                _values[key] = expiration;
                _bounds.Add(expiration);
            }

            _journal.Add((key, existed, previous));
            if (existed)
                recalculateBounds |= _bounds.Remove(previous);
            changed = true;
        }

        if (recalculateBounds)
            _bounds = Bounds.Calculate(_values);

        return count;
    }

    public void Commit()
    {
        _applying = false;
        _journaledValues = null;
        _journal.Clear();
        if (_journal.Capacity > 1024)
            _journal.Capacity = 0;
        TrimExcess();
    }

    /// <summary>
    /// Releases the capacity left behind when most members were removed or expired, so a shrunken list does not
    /// keep its peak storage. Trimming only once a quarter of the capacity is in use keeps the cost amortized.
    /// </summary>
    private void TrimExcess()
    {
        if (IsExposed || _values.GetType() != typeof(Dictionary<T, DateTime?>))
            return;

        var dictionary = (Dictionary<T, DateTime?>)_values;
        int capacity = dictionary.EnsureCapacity(0);
        if (capacity > 64 && dictionary.Count <= capacity / 4)
            dictionary.TrimExcess();
    }

    /// <summary>
    /// Undoes the changes made by the last journaled <see cref="Apply"/>. Expired members it dropped stay dropped;
    /// nothing can observe them, and the stored entry keeps its size until the next accepted update.
    /// </summary>
    public void Rollback()
    {
        if (_journaledValues is not null)
        {
            // The update began on a dictionary a caller holds; discard the copy instead of undoing into it.
            _values = _journaledValues;
            _exposedValues = _values;
        }
        else
        {
            for (int index = _journal.Count - 1; index >= 0; index--)
            {
                var (key, existed, expiration) = _journal[index];
                if (existed)
                    _values[key] = expiration;
                else
                    _values.Remove(key);
            }
        }

        _bounds = _journaledExpired && !IsExposed ? Bounds.Calculate(_values) : _journaledBounds;
        Commit();
    }

    /// <summary>Returns the live members, or one page of them, reading the storage under its lock.</summary>
    public CacheValue<ICollection<T>> Read(DateTime utcNow, int? page, int pageSize)
    {
        lock (SyncRoot)
        {
            // Expose may have handed this dictionary to a caller who changes it; only trust bounds when private.
            bool checkExpiration = IsExposed || _bounds.Earliest < utcNow;
            if (_values is not Dictionary<T, DateTime?> values)
                return ReadOther(utcNow, page, pageSize);

            if (page.HasValue)
                return ReadPage(values, utcNow, page.Value, pageSize, checkExpiration);

            var keys = ReadValues(values, utcNow, checkExpiration);
            return new CacheValue<ICollection<T>>(keys, keys.Length > 0);
        }
    }

    /// <summary>
    /// Reads an adopted dictionary of another type directly, since copying it into a <see cref="Dictionary{TKey,TValue}"/>
    /// would apply default equality, which its own comparer may disagree with.
    /// </summary>
    private CacheValue<ICollection<T>> ReadOther(DateTime utcNow, int? page, int pageSize)
    {
        var keys = new List<T>(_values.Count);
        foreach (var pair in _values)
        {
            if (pair.Value is null || pair.Value >= utcNow)
                keys.Add(pair.Key);
        }

        if (!page.HasValue)
            return new CacheValue<ICollection<T>>(keys.ToArray(), keys.Count > 0);

        int skip = Math.Max(0, unchecked((page.Value - 1) * pageSize));
        return new CacheValue<ICollection<T>>(keys.Skip(skip).Take(Math.Max(0, pageSize)).ToArray(), keys.Count > 0);
    }

    /// <summary>Moves to a private copy before the first change to a dictionary that a caller may hold.</summary>
    private void PrepareForChange()
    {
        if (!IsExposed)
            return;

        _journaledValues ??= _values;
        _values = CopyDictionary(_values);
        _bounds = Bounds.Calculate(_values);
        _exposedValues = null;
    }

    /// <summary>Drops expired members and recalculates the bounds from the survivors in one pass.</summary>
    private int RemoveExpired(DateTime utcNow)
    {
        if (IsExposed)
            _bounds = Bounds.Calculate(_values);
        if (!(_bounds.Earliest < utcNow))
            return 0;

        PrepareForChange();
        int expired = 0;
        var bounds = new Bounds();
        if (_values.GetType() == typeof(Dictionary<T, DateTime?>))
        {
            // Dictionary allows removing the current entry while enumerating, so pruning needs no buffer.
            var dictionary = (Dictionary<T, DateTime?>)_values;
            foreach (var pair in dictionary)
            {
                if (pair.Value < utcNow)
                {
                    dictionary.Remove(pair.Key);
                    expired++;
                }
                else
                {
                    bounds.Add(pair.Value);
                }
            }
        }
        else
        {
            List<T>? expiredKeys = null;
            foreach (var pair in _values)
            {
                if (pair.Value < utcNow)
                    (expiredKeys ??= []).Add(pair.Key);
                else
                    bounds.Add(pair.Value);
            }

            if (expiredKeys is not null)
            {
                foreach (var key in expiredKeys)
                    _values.Remove(key);
                expired = expiredKeys.Count;
            }
        }

        _bounds = bounds;
        return expired;
    }

    /// <summary>
    /// Copies the members, keeping the dictionary type and comparer. Exact framework types are copied directly;
    /// subclasses and other implementations are cloned so their type and behavior survive.
    /// </summary>
    private static IDictionary<T, DateTime?> CopyDictionary(IDictionary<T, DateTime?> values)
    {
        var type = values.GetType();
        if (type == typeof(Dictionary<T, DateTime?>))
        {
            var dictionary = (Dictionary<T, DateTime?>)values;
            return new Dictionary<T, DateTime?>(dictionary, dictionary.Comparer);
        }

        if (type == typeof(OrderedDictionary<T, DateTime?>))
        {
            var ordered = (OrderedDictionary<T, DateTime?>)values;
            return new OrderedDictionary<T, DateTime?>(ordered, ordered.Comparer);
        }

        if (type == typeof(SortedDictionary<T, DateTime?>))
        {
            var sorted = (SortedDictionary<T, DateTime?>)values;
            return new SortedDictionary<T, DateTime?>(sorted, sorted.Comparer);
        }

        if (type == typeof(SortedList<T, DateTime?>))
        {
            var sorted = (SortedList<T, DateTime?>)values;
            return new SortedList<T, DateTime?>(sorted, sorted.Comparer);
        }

        if (type == typeof(ConcurrentDictionary<T, DateTime?>))
        {
            var concurrent = (ConcurrentDictionary<T, DateTime?>)values;
            return new ConcurrentDictionary<T, DateTime?>(concurrent, concurrent.Comparer);
        }

        return (IDictionary<T, DateTime?>)InMemoryCacheClient.CacheEntry.CloneValue(values, forceDictionaryRebuild: false)!;
    }

    private static bool IsSameExpiration(DateTime? left, DateTime? right)
        => left.HasValue == right.HasValue && (!left.HasValue || left.Value.Ticks == right!.Value.Ticks && left.Value.Kind == right.Value.Kind);

    /// <summary>
    /// Tracks enough about member expirations to derive the entry expiration and to skip scanning for expired
    /// members until one can have expired. <see cref="Earliest"/> may be earlier than any remaining member,
    /// which only causes an extra scan; <see cref="Latest"/> is always exact.
    /// </summary>
    private struct Bounds
    {
        public int PermanentCount;
        public DateTime? Earliest;
        public DateTime? Latest;
        private int _latestCount;

        public static Bounds Calculate(IDictionary<T, DateTime?> values)
        {
            var bounds = new Bounds();
            foreach (var pair in values)
                bounds.Add(pair.Value, replaceEqualLatest: false);
            return bounds;
        }

        /// <param name="expiration">The member expiration being tracked.</param>
        /// <param name="replaceEqualLatest">
        /// Whether an expiration with the same ticks as <see cref="Latest"/> replaces it, so a newly written
        /// <see cref="DateTimeKind"/> is reported instead of the one it overwrote.
        /// </param>
        public void Add(DateTime? expiration, bool replaceEqualLatest = true)
        {
            if (expiration is not { } value)
            {
                PermanentCount++;
                return;
            }

            if (Earliest is null || value < Earliest)
                Earliest = value;

            if (Latest is null || value > Latest)
            {
                Latest = value;
                _latestCount = 1;
            }
            else if (value == Latest)
            {
                if (replaceEqualLatest)
                    Latest = value;
                _latestCount++;
            }
        }

        /// <summary>Forgets a member's expiration and returns <c>true</c> when the bounds must be recalculated.</summary>
        public bool Remove(DateTime? expiration)
        {
            if (expiration is null)
            {
                PermanentCount--;
                return false;
            }

            return expiration == Latest && --_latestCount is 0;
        }
    }

    public static CacheValue<ICollection<T>> ReadPage(Dictionary<T, DateTime?> dictionary, DateTime utcNow, int page, int pageSize, bool checkExpiration = true)
    {
        var result = new ListPage(dictionary.Count, page, pageSize);
        foreach (var pair in dictionary)
        {
            if (checkExpiration && pair.Value < utcNow)
                continue;
            if (result.TrySkip())
                continue;
            result.Add(pair.Key);
            if (result.IsFull)
                break;
        }
        return result.ToCacheValue();
    }

    private struct ListPage
    {
        private int _skip;
        private readonly int _initialSkip;
        private readonly int _capacity;
        private T[]? _values;
        private int _count;
        private bool _hasValue;

        public ListPage(int count, int page, int pageSize)
        {
            _skip = Math.Max(0, unchecked((page - 1) * pageSize));
            _initialSkip = _skip;
            _capacity = Math.Min(Math.Max(0, pageSize), Math.Max(0, count - _skip));
        }

        public bool IsFull => _hasValue && _count == _capacity;
        public bool CanSkip(int count) => _capacity > 0 && _skip >= count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySkip(int count = 1)
        {
            if (!CanSkip(count))
                return false;

            _skip -= count;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T value)
        {
            _hasValue = true;
            if (_capacity is 0)
                return;

            _values ??= new T[_capacity];
            _values[_count++] = value;
        }

        public CacheValue<ICollection<T>> ToCacheValue()
        {
            var values = _values ?? [];
            if (_count != values.Length)
                Array.Resize(ref values, _count);
            return new CacheValue<ICollection<T>>(values, _hasValue || _skip < _initialSkip);
        }
    }

    public static T[] ReadValues(Dictionary<T, DateTime?> dictionary, DateTime utcNow, bool checkExpiration = true)
    {
        var result = new T[dictionary.Count];
        int count = 0;
        if (checkExpiration)
        {
            foreach (var pair in dictionary)
                if (pair.Value is null || pair.Value >= utcNow)
                    result[count++] = pair.Key;
        }
        else
        {
            dictionary.Keys.CopyTo(result, 0);
            count = result.Length;
        }
        if (count != result.Length)
            Array.Resize(ref result, count);
        return result;
    }
}
