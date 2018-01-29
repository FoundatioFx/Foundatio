using System;

namespace Foundatio {
    public interface IOptionsBuilder {
        object Target { get; }
    }

    public static class OptionsBuilderExtensions {
        public static T Target<T>(this IOptionsBuilder builder) {
            return (T)builder.Target;
        }
    }

    public class OptionsBuilder<T> : IOptionsBuilder where T : class, new() {
        public T Target { get; } = new T();
        object IOptionsBuilder.Target => Target;
    }

    public delegate T Builder<T>(T builder = default(T)) where T: class, IOptionsBuilder, new();
}
