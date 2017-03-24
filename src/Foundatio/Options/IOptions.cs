using System;
using System.Collections;
using System.Collections.Generic;
using Foundatio.Extensions;

namespace Foundatio.Options {
    public interface IOptions {
        IOptionsDictionary Values { get; }
    }

    public interface IOptionsDictionary : IEnumerable<KeyValuePair<string, object>> {
        void Set(string name, object value);
        bool Contains(string name);
        bool Remove(string name);
        T Get<T>(string name, T defaultValue = default(T));
    }

    public class OptionsDictionary : IOptionsDictionary {
        protected readonly IDictionary<string, object> _options = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public void Set(string name, object value) {
            _options[name] = value;
        }

        public bool Contains(string name) {
            return _options.ContainsKey(name);
        }

        public bool Remove(string name) {
            return _options.Remove(name);
        }

        public T Get<T>(string name, T defaultValue) {
            if (!_options.ContainsKey(name))
                return defaultValue;

            object data = _options[name];
            if (!(data is T)) {
                try {
                    return data.ToType<T>();
                } catch {
                    throw new ArgumentException($"Option \"{name}\" is not compatible with the requested type \"{typeof(T).FullName}\".");
                }
            }

            return (T)data;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() {
            return _options.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return _options.GetEnumerator();
        }
    }

    public abstract class OptionsBase : IOptions {
        public IOptionsDictionary Values { get; } = new OptionsDictionary();
    }

    public static class OptionsExtensions {
        public static T BuildOption<T>(this T options, string name, object value) where T : IOptions {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            options.Values.Set(name, value);
            return options;
        }

        public static T SafeGetOption<T>(this IOptions options, string name, T defaultValue = default(T)) {
            if (options == null)
                return defaultValue;

            return options.Values.Get(name, defaultValue);
        }

        public static bool SafeHasOption(this IOptions options, string name) {
            if (options == null)
                return false;

            return options.Values.Contains(name);
        }

        public static ICollection<T> SafeGetCollection<T>(this IOptions options, string name) {
            if (options == null)
                return new List<T>();

            return options.Values.Get(name, new List<T>());
        }

        public static TOptions AddCollectionOptionValue<TOptions, TValue>(this TOptions options, string name, TValue value) where TOptions : IOptions {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var setOption = options.SafeGetOption(name, new List<TValue>());
            setOption.Add(value);
            options.Values.Set(name, setOption);

            return options;
        }

        public static TOptions AddCollectionOptionValue<TOptions, TValue>(this TOptions options, string name, IEnumerable<TValue> values) where TOptions : IOptions {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var setOption = options.SafeGetOption(name, new List<TValue>());
            setOption.AddRange(values);
            options.Values.Set(name, setOption);

            return options;
        }

        public static ISet<T> SafeGetSet<T>(this IOptions options, string name) {
            if (options == null)
                return new HashSet<T>();

            return options.Values.Get(name, new HashSet<T>());
        }

        public static T AddSetOptionValue<T>(this T options, string name, string value) where T : IOptions {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var setOption = options.SafeGetOption(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            setOption.Add(value);
            options.Values.Set(name, setOption);

            return options;
        }

        public static T AddSetOptionValue<T>(this T options, string name, IEnumerable<string> values) where T : IOptions {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var setOption = options.SafeGetOption(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            setOption.AddRange(values);
            options.Values.Set(name, setOption);

            return options;
        }

        public static T Clone<T>(this IOptions source) where T : IOptions, new() {
            var clone = new T();

            if (source != null)
                foreach (var kvp in source.Values)
                    clone.Values.Set(kvp.Key, kvp.Value);

            return clone;
        }

        public static T MergeFrom<T>(this T target, IOptions source, bool overrideExisting = true) where T : IOptions {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (source == null)
                return target;

            foreach (var kvp in source.Values) {
                // TODO: Collection option values should get added to instead of replaced
                if (overrideExisting || !target.Values.Contains(kvp.Key))
                    target.Values.Set(kvp.Key, kvp.Value);
            }

            return target;
        }
    }
}