using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Resilience;

/// <summary>
/// A builder class for configuring and constructing a <see cref="ResiliencePolicyProvider"/> with custom and default resilience policies.
/// </summary>
public class ResiliencePolicyProviderBuilder
{
    private readonly ResiliencePolicyProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResiliencePolicyProviderBuilder"/> class.
    /// </summary>
    /// <param name="timeProvider">An optional <see cref="TimeProvider"/> instance for controlling time-related behavior.</param>
    /// <param name="loggerFactory">An optional <see cref="ILoggerFactory"/> instance for logging.</param>
    public ResiliencePolicyProviderBuilder(TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null)
    {
        _provider = new ResiliencePolicyProvider(timeProvider, loggerFactory);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResiliencePolicyProviderBuilder"/> class with an existing provider.
    /// </summary>
    /// <param name="provider">The existing <see cref="ResiliencePolicyProvider"/> to use.</param>
    public ResiliencePolicyProviderBuilder(ResiliencePolicyProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Adds or replaces a resilience policy with the specified name.
    /// </summary>
    /// <param name="name">The name of the policy.</param>
    /// <param name="policy">The <see cref="IResiliencePolicy"/> instance to associate with the name.</param>
    /// <returns>The configured <see cref="ResiliencePolicyProvider"/>.</returns>
    public ResiliencePolicyProvider WithPolicy(string name, IResiliencePolicy policy)
    {
        return _provider.WithPolicy(name, policy);
    }

    /// <summary>
    /// Adds or replaces a resilience policy with the specified name using a builder action.
    /// </summary>
    /// <param name="name">The name of the policy.</param>
    /// <param name="builder">An action to configure the <see cref="IResiliencePolicy"/> for the name.</param>
    /// <returns>The configured <see cref="ResiliencePolicyProvider"/>.</returns>
    public ResiliencePolicyProvider WithPolicy(string name, Action<ResiliencePolicyBuilder> builder = null)
    {
        return _provider.WithPolicy(name, builder);
    }

    /// <summary>
    /// Adds or replaces a resilience policy for the specified type.
    /// </summary>
    /// <param name="targetType">The target type for the policy.</param>
    /// <param name="policy">The <see cref="IResiliencePolicy"/> instance to associate with the type.</param>
    /// <returns>The configured <see cref="ResiliencePolicyProvider"/>.</returns>
    public ResiliencePolicyProvider WithPolicy(Type targetType, IResiliencePolicy policy)
    {
        return _provider.WithPolicy(targetType, policy);
    }

    /// <summary>
    /// Adds or replaces a resilience policy for the specified type using a builder action.
    /// </summary>
    /// <param name="targetType">The target type for the policy.</param>
    /// <param name="builder">An action to configure the <see cref="IResiliencePolicy"/> for the type.</param>
    /// <returns>The configured <see cref="ResiliencePolicyProvider"/>.</returns>
    public ResiliencePolicyProvider WithPolicy(Type targetType, Action<ResiliencePolicyBuilder> builder = null)
    {
        return _provider.WithPolicy(targetType, builder);
    }

    /// <summary>
    /// Adds or replaces a resilience policy for the specified generic type.
    /// </summary>
    /// <typeparam name="T">The target type for the policy.</typeparam>
    /// <param name="policy">The <see cref="IResiliencePolicy"/> instance to associate with the type.</param>
    /// <returns>The configured <see cref="ResiliencePolicyProvider"/>.</returns>
    public ResiliencePolicyProvider WithPolicy<T>(IResiliencePolicy policy)
    {
        return _provider.WithPolicy<T>(policy);
    }

    /// <summary>
    /// Adds or replaces a resilience policy for the specified generic type using a builder action.
    /// </summary>
    /// <typeparam name="T">The target type for the policy.</typeparam>
    /// <param name="builder">An action to configure the <see cref="IResiliencePolicy"/> for the type.</param>
    /// <returns>The configured <see cref="ResiliencePolicyProvider"/>.</returns>
    public ResiliencePolicyProvider WithPolicy<T>(Action<ResiliencePolicyBuilder> builder = null)
    {
        return _provider.WithPolicy<T>(builder);
    }

    /// <summary>
    /// Sets the default resilience policy.
    /// </summary>
    /// <param name="policy">The default <see cref="IResiliencePolicy"/> to use.</param>
    /// <returns>The configured <see cref="ResiliencePolicyProvider"/>.</returns>
    public ResiliencePolicyProvider WithDefaultPolicy(IResiliencePolicy policy)
    {
        return _provider.WithDefaultPolicy(policy);
    }

    /// <summary>
    /// Sets the default resilience policy using a builder action.
    /// </summary>
    /// <param name="builder">An action to configure the default <see cref="IResiliencePolicy"/>.</param>
    /// <returns>The configured <see cref="ResiliencePolicyProvider"/>.</returns>
    public ResiliencePolicyProvider WithDefaultPolicy(Action<ResiliencePolicyBuilder> builder = null)
    {
        return _provider.WithDefaultPolicy(builder);
    }

    /// <summary>
    /// Builds and returns the configured <see cref="ResiliencePolicyProvider"/> instance.
    /// </summary>
    /// <returns>The constructed <see cref="ResiliencePolicyProvider"/>.</returns>
    public ResiliencePolicyProvider Build() => _provider;
}
