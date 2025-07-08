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
    /// <typeparam name="T">The type for which to get the policy.</typeparam>
    /// <param name="provider">The resilience policy provider.</param>
    /// <param name="logger">Optional logger to use for the created policy if not found in the provider.</param>
    /// <param name="timeProvider">Optional time provider to use for the created policy if not found in the provider.</param>
    /// <returns>The resolved or newly created <see cref="IResiliencePolicy"/>.</returns>
    public static IResiliencePolicy GetPolicy<T>(this IResiliencePolicyProvider provider, ILogger logger = null, TimeProvider timeProvider = null)
    {
        return GetPolicy(provider, [typeof(T)], null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy for the specified type from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <typeparam name="T">The type for which to get the policy.</typeparam>
    /// <param name="provider">The resilience policy provider.</param>
    /// <param name="fallbackBuilder">An action to configure the fallback <see cref="IResiliencePolicy"/> if not found in the provider.</param>
    /// <param name="logger">Optional logger to use for the created policy if not found in the provider.</param>
    /// <param name="timeProvider">Optional time provider to use for the created policy if not found in the provider.</param>
    /// <returns>The resolved or newly created <see cref="IResiliencePolicy"/>.</returns>
    public static IResiliencePolicy GetPolicy<T>(this IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        return GetPolicy(provider, [typeof(T)], fallbackBuilder, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new default configuration one if not found.
    /// </summary>
    /// <typeparam name="T1">The first type to check for a policy.</typeparam>
    /// <typeparam name="T2">The second type to check for a policy.</typeparam>
    /// <param name="provider">The resilience policy provider.</param>
    /// <param name="logger">Optional logger to use for the created policy if not found in the provider.</param>
    /// <param name="timeProvider">Optional time provider to use for the created policy if not found in the provider.</param>
    /// <returns>The resolved or newly created <see cref="IResiliencePolicy"/>.</returns>
    public static IResiliencePolicy GetPolicy<T1, T2>(this IResiliencePolicyProvider provider, ILogger logger = null, TimeProvider timeProvider = null)
    {
        return GetPolicy(provider, [typeof(T1), typeof(T2)], null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <typeparam name="T1">The first type to check for a policy.</typeparam>
    /// <typeparam name="T2">The second type to check for a policy.</typeparam>
    /// <param name="provider">The resilience policy provider.</param>
    /// <param name="fallbackBuilder">An action to configure the fallback <see cref="IResiliencePolicy"/> if not found in the provider.</param>
    /// <param name="logger">Optional logger to use for the created policy if not found in the provider.</param>
    /// <param name="timeProvider">Optional time provider to use for the created policy if not found in the provider.</param>
    /// <returns>The resolved or newly created <see cref="IResiliencePolicy"/>.</returns>
    public static IResiliencePolicy GetPolicy<T1, T2>(this IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        return GetPolicy(provider, [typeof(T1), typeof(T2)], fallbackBuilder, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new default configuration one if not found.
    /// </summary>
    /// <typeparam name="T1">The first type to check for a policy.</typeparam>
    /// <typeparam name="T2">The second type to check for a policy.</typeparam>
    /// <typeparam name="T3">The third type to check for a policy.</typeparam>
    /// <param name="provider">The resilience policy provider.</param>
    /// <param name="logger">Optional logger to use for the created policy if not found in the provider.</param>
    /// <param name="timeProvider">Optional time provider to use for the created policy if not found in the provider.</param>
    /// <returns>The resolved or newly created <see cref="IResiliencePolicy"/>.</returns>
    public static IResiliencePolicy GetPolicy<T1, T2, T3>(this IResiliencePolicyProvider provider, ILogger logger = null, TimeProvider timeProvider = null)
    {
        return GetPolicy(provider, [typeof(T1), typeof(T2), typeof(T3)], null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <typeparam name="T1">The first type to check for a policy.</typeparam>
    /// <typeparam name="T2">The second type to check for a policy.</typeparam>
    /// <typeparam name="T3">The third type to check for a policy.</typeparam>
    /// <param name="provider">The resilience policy provider.</param>
    /// <param name="fallbackBuilder">An action to configure the fallback <see cref="IResiliencePolicy"/> if not found in the provider.</param>
    /// <param name="logger">Optional logger to use for the created policy if not found in the provider.</param>
    /// <param name="timeProvider">Optional time provider to use for the created policy if not found in the provider.</param>
    /// <returns>The resolved or newly created <see cref="IResiliencePolicy"/>.</returns>
    public static IResiliencePolicy GetPolicy<T1, T2, T3>(this IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        return GetPolicy(provider, [typeof(T1), typeof(T2), typeof(T3)], fallbackBuilder, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy for the specified type from the provider, or creates a new default configuration one if not found.
    /// </summary>
    /// <param name="provider">The resilience policy provider.</param>
    /// <param name="targetType">The type for which to get the policy.</param>
    /// <param name="logger">Optional logger to use for the created policy if not found in the provider.</param>
    /// <param name="timeProvider">Optional time provider to use for the created policy if not found in the provider.</param>
    /// <returns>The resolved or newly created <see cref="IResiliencePolicy"/>.</returns>
    public static IResiliencePolicy GetPolicy(this IResiliencePolicyProvider provider, Type targetType, ILogger logger = null, TimeProvider timeProvider = null)
    {
        return GetPolicy(provider, [targetType], null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <param name="provider">The resilience policy provider.</param>
    /// <param name="targetTypes">The types to check for a policy, in order.</param>
    /// <param name="fallbackBuilder">An action to configure the fallback <see cref="IResiliencePolicy"/> if not found in the provider.</param>
    /// <param name="logger">Optional logger to use for the created policy if not found in the provider.</param>
    /// <param name="timeProvider">Optional time provider to use for the created policy if not found in the provider.</param>
    /// <returns>The resolved or newly created <see cref="IResiliencePolicy"/>.</returns>
    public static IResiliencePolicy GetPolicy(this IResiliencePolicyProvider provider, Type[] targetTypes, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        if (provider != null)
        {
            foreach (var targetType in targetTypes)
            {
                IResiliencePolicy policy = provider.GetPolicy(targetType.GetFriendlyTypeName(), false);
                if (policy != null)
                    return policy;
            }
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

    /// <summary>
    /// Gets the <see cref="IResiliencePolicyProvider"/> from the specified object if it implements <see cref="IHaveResiliencePolicyProvider"/>.
    /// </summary>
    /// <param name="target">The object to retrieve the policy provider from.</param>
    /// <returns>The <see cref="IResiliencePolicyProvider"/> if available; otherwise, <c>null</c>.</returns>
    public static IResiliencePolicyProvider GetResiliencePolicyProvider(this object target)
    {
        return target is IHaveResiliencePolicyProvider accessor ? accessor.ResiliencePolicyProvider : null;
    }

    /// <summary>
    /// Executes the specified asynchronous action using the given resilience policy.
    /// </summary>
    /// <param name="policy">The resilience policy to use for execution.</param>
    /// <param name="action">The asynchronous action to execute.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    public static ValueTask ExecuteAsync(this IResiliencePolicy policy, Func<ValueTask> action, CancellationToken cancellationToken = default)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return policy.ExecuteAsync(_ => action(), cancellationToken);
    }

    /// <summary>
    /// Executes the specified asynchronous action using the given resilience policy and returns a result.
    /// </summary>
    /// <typeparam name="T">The type of the result returned by the action.</typeparam>
    /// <param name="policy">The resilience policy to use for execution.</param>
    /// <param name="action">The asynchronous action to execute.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{T}"/> representing the asynchronous operation and its result.</returns>
    public static ValueTask<T> ExecuteAsync<T>(this IResiliencePolicy policy, Func<ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return policy.ExecuteAsync(_ => action(), cancellationToken);
    }
}
