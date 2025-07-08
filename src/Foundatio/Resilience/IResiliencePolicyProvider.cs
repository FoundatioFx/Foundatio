namespace Foundatio.Resilience;

public interface IResiliencePolicyProvider
{
    IResiliencePolicy GetDefaultPolicy();
    IResiliencePolicy GetPolicy(string name, bool useDefault = true);
}
