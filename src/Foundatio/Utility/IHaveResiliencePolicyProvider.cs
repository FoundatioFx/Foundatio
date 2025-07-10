using Foundatio.Resilience;

namespace Foundatio.Utility;

public interface IHaveResiliencePolicyProvider
{
    IResiliencePolicyProvider ResiliencePolicyProvider { get; }
}

public static class HaveResiliencePolicyExtensions
{
    public static IResiliencePolicyProvider GetResiliencePolicyProvider(this object target)
    {
        return target is IHaveResiliencePolicyProvider accessor ? accessor.ResiliencePolicyProvider : null;
    }
}
