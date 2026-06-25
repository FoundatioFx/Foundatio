using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Foundatio.Messaging;

public sealed class MessageHeaders : IReadOnlyDictionary<string, string>
{
    public static MessageHeaders Empty { get; } = new(FrozenDictionary<string, string>.Empty);

    private readonly FrozenDictionary<string, string> _headers;

    private MessageHeaders(FrozenDictionary<string, string> headers)
    {
        _headers = headers;
    }

    public string this[string key] => _headers[key];
    public IEnumerable<string> Keys => _headers.Keys;
    public IEnumerable<string> Values => _headers.Values;
    public int Count => _headers.Count;

    public static MessageHeaders Create(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers is MessageHeaders messageHeaders)
            return messageHeaders;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            ArgumentException.ThrowIfNullOrEmpty(header.Key);
            ArgumentNullException.ThrowIfNull(header.Value);
            values[header.Key] = header.Value;
        }

        return values.Count == 0
            ? Empty
            : new MessageHeaders(values.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    public bool ContainsKey(string key)
    {
        return _headers.ContainsKey(key);
    }

    public bool TryGetValue(string key, out string value)
    {
        return _headers.TryGetValue(key, out value!);
    }

    public string? GetValueOrDefault(string key)
    {
        return _headers.GetValueOrDefault(key);
    }

    public Builder ToBuilder()
    {
        return new Builder(_headers);
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        return _headers.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public sealed class Builder
    {
        private readonly Dictionary<string, string> _headers;

        internal Builder(IEnumerable<KeyValuePair<string, string>> headers)
        {
            _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        }

        public Builder Add(string key, string value)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(value);
            _headers.Add(key, value);
            return this;
        }

        public Builder Set(string key, string value)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(value);
            _headers[key] = value;
            return this;
        }

        public Builder SetIfMissing(string key, string value)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(value);
            _headers.TryAdd(key, value);
            return this;
        }

        public bool Remove(string key)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            return _headers.Remove(key);
        }

        public MessageHeaders Build()
        {
            return Create(_headers);
        }
    }
}
