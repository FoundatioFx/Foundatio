using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Foundatio.Caching;

/// <summary>
/// Tracks writes whose distributed list add has started but whose local update is still pending.
/// Overlapping adds or an intervening mutation make the local list unsafe to update from the returned count.
/// </summary>
internal sealed class PendingListAdds
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    // Register and retire writers under the same lock. Other cache operations only mark an existing entry.
    public Entry Begin(string key)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Count++;
                entry.Invalidated = true;
                return entry;
            }

            entry = new Entry();
            _entries[key] = entry;
            return entry;
        }
    }

    public bool End(string key)
    {
        lock (_entries)
        {
            var entry = _entries[key];
            if (--entry.Count == 0)
                _entries.TryRemove(key, out _);

            return entry.Invalidated;
        }
    }

    public void Invalidate(string key)
    {
        if (_entries.TryGetValue(key, out var entry))
            entry.Invalidated = true;
    }

    public void Invalidate(IEnumerable<string> keys)
    {
        foreach (string key in keys)
            Invalidate(key);
    }

    public void InvalidateByPrefix(string prefix)
    {
        foreach (var (key, entry) in _entries)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                entry.Invalidated = true;
        }
    }

    internal sealed class Entry
    {
        public int Count = 1;
        public volatile bool Invalidated;
    }
}
