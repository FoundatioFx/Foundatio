using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility.Resilience;
using Polly;
using Polly.Retry;
using ResiliencePipelineBuilder = Polly.ResiliencePipelineBuilder;

namespace Foundatio.Tests.Utility;

public class PollyResiliencePolicyProvider : IResiliencePolicyProvider
{
    private readonly ConcurrentDictionary<string, IResiliencePolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private IResiliencePolicy _defaultPolicy = new PollyResiliencePolicy(new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions()).Build());

    public PollyResiliencePolicyProvider WithDefaultPolicy(IResiliencePolicy policy)
    {
        _defaultPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public PollyResiliencePolicyProvider WithPolicy(string name, ResiliencePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(name);

        ArgumentNullException.ThrowIfNull(pipeline);

        _policies[name] = new PollyResiliencePolicy(pipeline);
        return this;
    }

    public PollyResiliencePolicyProvider WithPolicy(string name, Action<ResiliencePipelineBuilder> pipelineBuilder)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        if (pipelineBuilder == null)
            throw new ArgumentNullException(nameof(pipelineBuilder));

        var builder = new ResiliencePipelineBuilder();
        pipelineBuilder(builder);

        _policies[name] = new PollyResiliencePolicy(builder.Build());
        return this;
    }

    public PollyResiliencePolicyProvider WithPolicy<T>(ResiliencePipeline policy)
    {
        string name = typeof(T).FullName;
        return WithPolicy(name, policy);
    }

    public PollyResiliencePolicyProvider WithPolicy<T>(Action<ResiliencePipelineBuilder> builder)
    {
        string name = typeof(T).FullName;
        return WithPolicy(name, builder);
    }

    public IResiliencePolicy GetDefaultPolicy() => _defaultPolicy;

    public IResiliencePolicy GetPolicy(string name, bool useDefault = true)
    {
        if (String.IsNullOrEmpty(name))
            throw new ArgumentNullException(nameof(name));

        return _policies.TryGetValue(name, out var policy) ? policy : useDefault ? _defaultPolicy : null;
    }

    private class PollyResiliencePolicy(ResiliencePipeline pipeline) : IResiliencePolicy
    {
        public ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
        {
            return pipeline.ExecuteAsync(action, cancellationToken);
        }

        public ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
        {
            return pipeline.ExecuteAsync(action, cancellationToken);
        }
    }
}
