namespace Foundatio.Resilience;

/// <summary>
/// Provides methods for retrieving and managing resilience policies, such as retry, circuit breaker, or timeout policies.
/// </summary>
public interface IResiliencePolicyProvider
{
    /// <summary>
    /// Gets the default resilience policy.
    /// </summary>
    /// <returns>The default <see cref="IResiliencePolicy"/> instance.</returns>
    IResiliencePolicy GetDefaultPolicy();

    /// <summary>
    /// Gets a named resilience policy.
    /// </summary>
    /// <param name="name">The name of the policy to retrieve.</param>
    /// <param name="useDefault">If true, returns the default policy if the named policy is not found; otherwise, returns null.</param>
    /// <returns>The <see cref="IResiliencePolicy"/> instance, or null if not found and <paramref name="useDefault"/> is false.</returns>
    IResiliencePolicy GetPolicy(string name, bool useDefault = true);
}
