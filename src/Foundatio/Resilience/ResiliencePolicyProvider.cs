using System;
using System.Collections.Concurrent;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Resilience;

/// <summary>
/// Provides a mechanism for managing and retrieving resilience policies by name or type.
/// Supports setting default and named policies, as well as configuring policies using builders.
/// </summary>
public class ResiliencePolicyProvider : IResiliencePolicyProvider
{
    private readonly ConcurrentDictionary<string, IResiliencePolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private IResiliencePolicy _defaultPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;

    public ResiliencePolicyProvider(TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _defaultPolicy = new ResiliencePolicy(_loggerFactory.CreateLogger<ResiliencePolicy>(), _timeProvider);
    }

    /// <summary>
    /// Sets the default resilience policy.
    /// </summary>
    /// <param name="policy">The default <see cref="IResiliencePolicy"/> to use.</param>
    /// <returns>The current <see cref="ResiliencePolicyProvider"/> instance.</returns>
    public ResiliencePolicyProvider WithDefaultPolicy(IResiliencePolicy policy)
    {
        _defaultPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <summary>
    /// Configures and sets the default resilience policy using a builder action.
    /// </summary>
    /// <param name="builder">An action to configure the <see cref="IResiliencePolicy"/>.</param>
    /// <returns>The current <see cref="ResiliencePolicyProvider"/> instance.</returns>
    public ResiliencePolicyProvider WithDefaultPolicy(Action<ResiliencePolicyBuilder> builder = null)
    {
        var policy = new ResiliencePolicy(_loggerFactory.CreateLogger<ResiliencePolicy>(), _timeProvider);
        var policyBuilder = new ResiliencePolicyBuilder(policy);
        builder?.Invoke(policyBuilder);

        _defaultPolicy = policy;
        return this;
    }

    /// <summary>
    /// Adds or replaces a named resilience policy.
    /// </summary>
    /// <param name="name">The name of the policy.</param>
    /// <param name="policy">The <see cref="IResiliencePolicy"/> instance.</param>
    /// <returns>The current <see cref="ResiliencePolicyProvider"/> instance.</returns>
    public ResiliencePolicyProvider WithPolicy(string name, IResiliencePolicy policy)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        _policies[name] = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <summary>
    /// Configures and adds or replaces a named resilience policy using a builder action.
    /// </summary>
    /// <param name="name">The name of the policy.</param>
    /// <param name="builder">An action to configure the <see cref="IResiliencePolicy"/>.</param>
    /// <returns>The current <see cref="ResiliencePolicyProvider"/> instance.</returns>
    public ResiliencePolicyProvider WithPolicy(string name, Action<ResiliencePolicyBuilder> builder = null)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        var policy = new ResiliencePolicy(_loggerFactory.CreateLogger<ResiliencePolicy>(), _timeProvider);
        var policyBuilder = new ResiliencePolicyBuilder(policy);
        builder?.Invoke(policyBuilder);

        _policies[name] = policy;
        return this;
    }

    /// <summary>
    /// Adds or replaces a resilience policy for the specified type.
    /// </summary>
    /// <typeparam name="T">The target type for the policy.</typeparam>
    /// <param name="policy">The <see cref="IResiliencePolicy"/> instance.</param>
    /// <returns>The current <see cref="ResiliencePolicyProvider"/> instance.</returns>
    public ResiliencePolicyProvider WithPolicy<T>(IResiliencePolicy policy)
    {
        string name = typeof(T).GetFriendlyTypeName();
        return WithPolicy(name, policy);
    }

    /// <summary>
    /// Configures and adds or replaces a resilience policy for the specified type using a builder action.
    /// </summary>
    /// <typeparam name="T">The target type for the policy.</typeparam>
    /// <param name="builder">An action to configure the <see cref="IResiliencePolicy"/>.</param>
    /// <returns>The current <see cref="ResiliencePolicyProvider"/> instance.</returns>
    public ResiliencePolicyProvider WithPolicy<T>(Action<ResiliencePolicyBuilder> builder = null)
    {
        string name = typeof(T).GetFriendlyTypeName();
        return WithPolicy(name, builder);
    }

    /// <summary>
    /// Adds or replaces a resilience policy for the specified type.
    /// </summary>
    /// <param name="targetType">The target type for the policy.</param>
    /// <param name="policy">The <see cref="IResiliencePolicy"/> instance.</param>
    /// <returns>The current <see cref="ResiliencePolicyProvider"/> instance.</returns>
    public ResiliencePolicyProvider WithPolicy(Type targetType, IResiliencePolicy policy)
    {
        string name = targetType.GetFriendlyTypeName();
        return WithPolicy(name, policy);
    }

    /// <summary>
    /// Configures and adds or replaces a resilience policy for the specified type using a builder action.
    /// </summary>
    /// <param name="targetType">The target type for the policy.</param>
    /// <param name="builder">An action to configure the <see cref="IResiliencePolicy"/>.</param>
    /// <returns>The current <see cref="ResiliencePolicyProvider"/> instance.</returns>
    public ResiliencePolicyProvider WithPolicy(Type targetType, Action<ResiliencePolicyBuilder> builder = null)
    {
        string name = targetType.GetFriendlyTypeName();
        return WithPolicy(name, builder);
    }

    /// <summary>
    /// Retrieves a resilience policy by name.
    /// </summary>
    /// <param name="name">The name of the policy.</param>
    /// <param name="useDefault">Whether to return the default policy if the named policy is not found.</param>
    /// <returns>The <see cref="IResiliencePolicy"/> instance, or null if not found and <paramref name="useDefault"/> is false.</returns>
    public IResiliencePolicy GetPolicy(string name, bool useDefault = true)
    {
        if (String.IsNullOrEmpty(name))
            throw new ArgumentNullException(nameof(name));

        return _policies.TryGetValue(name, out var policy) ? policy : useDefault ? _defaultPolicy : null;
    }

    /// <summary>
    /// Gets the default resilience policy.
    /// </summary>
    /// <returns>The default <see cref="IResiliencePolicy"/> instance.</returns>
    public IResiliencePolicy GetDefaultPolicy() => _defaultPolicy;
}

public static class DefaultResiliencePolicyProvider
{
    public static ResiliencePolicyProvider Instance { get; set; } = new();
}
