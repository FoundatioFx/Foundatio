using System;

namespace Foundatio;

public interface IOptionsBuilder
{
    object Target { get; }
}

public interface IOptionsBuilder<out T> : IOptionsBuilder
{
    T Build();
}

public static class OptionsBuilderExtensions
{
    public static T Target<T>(this IOptionsBuilder builder)
    {
        return (T)builder.Target;
    }
}

public class OptionsBuilder<T> : IOptionsBuilder<T> where T : class, new()
{
    public T Target { get; } = new T();
    object IOptionsBuilder.Target => Target;

    public virtual T Build()
    {
        return Target;
    }
}

public delegate TBuilder Builder<TBuilder, TOptions>(TBuilder builder) where TBuilder : class, IOptionsBuilder<TOptions>, new();
