using Foundatio.Resilience;

namespace Foundatio.Utility;

/// <summary>
/// Indicates that a type exposes a resilience policy provider for fault tolerance.
/// </summary>
public interface IHaveResiliencePolicyProvider
{
    /// <summary>
    /// Gets the resilience policy provider for retry, circuit breaker, and timeout policies.
    /// </summary>
    IResiliencePolicyProvider ResiliencePolicyProvider { get; }
}

public static class HaveResiliencePolicyExtensions
{
    public static IResiliencePolicyProvider GetResiliencePolicyProvider(this object target)
    {
        return target is IHaveResiliencePolicyProvider accessor ? accessor.ResiliencePolicyProvider : null;
    }
}
