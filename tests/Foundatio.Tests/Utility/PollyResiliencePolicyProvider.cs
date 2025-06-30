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

    public IResiliencePolicyProvider WithDefaultPolicy(IResiliencePolicy policy)
    {
        _defaultPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public IResiliencePolicyProvider WithPolicy(string name, ResiliencePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(name);

        ArgumentNullException.ThrowIfNull(pipeline);

        _policies[name] = new PollyResiliencePolicy(pipeline);
        return this;
    }

    public IResiliencePolicyProvider WithPolicy(string name, Action<ResiliencePipelineBuilder> pipelineBuilder)
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

    public IResiliencePolicy GetPolicy(string name = null)
    {
        return name == null ? _defaultPolicy : _policies.GetOrAdd(name, _ => _defaultPolicy);
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
