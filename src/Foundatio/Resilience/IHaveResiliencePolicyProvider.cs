namespace Foundatio.Resilience;

public interface IHaveResiliencePolicyProvider
{
    IResiliencePolicyProvider ResiliencePolicyProvider { get; }
}
