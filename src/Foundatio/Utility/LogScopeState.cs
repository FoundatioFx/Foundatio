using System;
using System.Collections;
using System.Collections.Generic;

namespace Foundatio.Utility;

/// <summary>
/// Log scope state with two properties, read by logging providers as key-value pairs like any structured scope. One
/// small object, instead of a dictionary, for scopes begun on every message or job.
/// </summary>
internal sealed class LogScopeState : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly KeyValuePair<string, object?> _first;
    private readonly KeyValuePair<string, object?> _second;

    public LogScopeState(string firstName, object? firstValue, string secondName, object? secondValue)
    {
        _first = new KeyValuePair<string, object?>(firstName, firstValue);
        _second = new KeyValuePair<string, object?>(secondName, secondValue);
    }

    public int Count => 2;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => _first,
        1 => _second,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        yield return _first;
        yield return _second;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"{_first.Key}:{_first.Value} {_second.Key}:{_second.Value}";
}
