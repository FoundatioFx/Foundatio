using System;
using System.Collections.Concurrent;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Resilience;

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
        _defaultPolicy = new ResiliencePolicy(_loggerFactory.CreateLogger<ResiliencePolicy>(), _timeProvider)
        {
            MaxAttempts = 5
        };
    }

    public ResiliencePolicyProvider WithDefaultPolicy(IResiliencePolicy policy)
    {
        _defaultPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public ResiliencePolicyProvider WithDefaultPolicy(Action<ResiliencePolicyBuilder> builder)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        var policy = new ResiliencePolicy(_loggerFactory.CreateLogger<ResiliencePolicy>(), _timeProvider);
        var policyBuilder = new ResiliencePolicyBuilder(policy);
        builder(policyBuilder);

        _defaultPolicy = policy;
        return this;
    }

    public ResiliencePolicyProvider WithPolicy(string name, IResiliencePolicy policy)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        _policies[name] = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public ResiliencePolicyProvider WithPolicy(string name, Action<ResiliencePolicyBuilder> builder)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        var policy = new ResiliencePolicy(_loggerFactory.CreateLogger<ResiliencePolicy>(), _timeProvider);
        var policyBuilder = new ResiliencePolicyBuilder(policy);
        builder(policyBuilder);

        _policies[name] = policy;
        return this;
    }

    public ResiliencePolicyProvider WithPolicy<T>(IResiliencePolicy policy)
    {
        string name = typeof(T).GetFriendlyTypeName();
        return WithPolicy(name, policy);
    }

    public ResiliencePolicyProvider WithPolicy<T>(Action<ResiliencePolicyBuilder> builder)
    {
        string name = typeof(T).GetFriendlyTypeName();
        return WithPolicy(name, builder);
    }

    public IResiliencePolicy GetDefaultPolicy() => _defaultPolicy;

    public IResiliencePolicy GetPolicy(string name, bool useDefault = true)
    {
        if (String.IsNullOrEmpty(name))
            throw new ArgumentNullException(nameof(name));

        return _policies.TryGetValue(name, out var policy) ? policy : useDefault ? _defaultPolicy : null;
    }
}
