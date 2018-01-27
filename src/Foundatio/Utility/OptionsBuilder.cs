using System;

namespace Foundatio {
    public interface IOptionsBuilder<out T> {
        T Target { get; }
    }

    public class OptionsBuilder<T> : IOptionsBuilder<T> where T : class, new() {
        public T Target { get; } = new T();

        public static T Build(Action<OptionsBuilder<T>> config) {
            var builder = new OptionsBuilder<T>();
            config?.Invoke(builder);
            return builder.Target;
        }
    }
}
