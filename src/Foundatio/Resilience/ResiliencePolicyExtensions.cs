using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Resilience;

public static class ResiliencePolicyExtensions
{
    /// <summary>
    /// Gets a resilience policy for the specified type from the provider, or creates a new default configuration one if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T>(this IResiliencePolicyProvider provider, ILogger logger = null, TimeProvider timeProvider = null)
    {
        IResiliencePolicy policy = provider?.GetPolicy(typeof(T).GetFriendlyTypeName(), false);
        return policy ?? GetDefaultPolicy(provider, null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy for the specified type from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="fallbackBuilder"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T>(this IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        IResiliencePolicy policy = provider?.GetPolicy(typeof(T).GetFriendlyTypeName(), false);
        return policy ?? GetDefaultPolicy(provider, fallbackBuilder, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new default configuration one if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T1, T2>(this IResiliencePolicyProvider provider, ILogger logger = null, TimeProvider timeProvider = null)
    {
        if (provider != null)
        {
            IResiliencePolicy policy = provider.GetPolicy(typeof(T1).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T2).GetFriendlyTypeName(), false);
            if (policy != null)
                return policy;
        }

        return GetDefaultPolicy(provider, null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="fallbackBuilder"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T1, T2>(this IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        if (provider != null)
        {
            IResiliencePolicy policy = provider.GetPolicy(typeof(T1).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T2).GetFriendlyTypeName(), false);
            if (policy != null)
                return policy;
        }

        return GetDefaultPolicy(provider, fallbackBuilder, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new default configuration one if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T1, T2, T3>(this IResiliencePolicyProvider provider, ILogger logger = null, TimeProvider timeProvider = null)
    {
        if (provider != null)
        {
            IResiliencePolicy policy = provider.GetPolicy(typeof(T1).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T2).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T3).GetFriendlyTypeName(), false);
            if (policy != null)
                return policy;
        }

        return GetDefaultPolicy(provider, null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="fallbackBuilder"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T1, T2, T3>(this IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        IResiliencePolicy policy;

        if (provider != null)
        {
            policy = provider.GetPolicy(typeof(T1).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T2).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T3).GetFriendlyTypeName(), false);
            if (policy != null)
                return policy;
        }

        return GetDefaultPolicy(provider, fallbackBuilder, logger, timeProvider);
    }

    private static IResiliencePolicy GetDefaultPolicy(IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder = null, ILogger logger = null, TimeProvider timeProvider = null)
    {
        if (provider != null && provider.GetType() != typeof(ResiliencePolicyProvider))
        {
            var defaultPolicy = provider.GetDefaultPolicy();
            if (defaultPolicy != null)
                return defaultPolicy;
        }

        var policy = new ResiliencePolicy(logger, timeProvider);
        fallbackBuilder?.Invoke(new ResiliencePolicyBuilder(policy));
        return policy;
    }

    public static IResiliencePolicyProvider GetResiliencePolicyProvider(this object target)
    {
        return target is IHaveResiliencePolicyProvider accessor ? accessor.ResiliencePolicyProvider : null;
    }

    public static ValueTask ExecuteAsync(this IResiliencePolicy policy, Func<ValueTask> action, CancellationToken cancellationToken = default)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return policy.ExecuteAsync(_ => action(), cancellationToken);
    }

    public static ValueTask<T> ExecuteAsync<T>(this IResiliencePolicy policy, Func<ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return policy.ExecuteAsync(_ => action(), cancellationToken);
    }
}
