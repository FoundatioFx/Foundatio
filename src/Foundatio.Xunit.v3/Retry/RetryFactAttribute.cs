#nullable enable

using Xunit;
using Xunit.v3;

namespace Foundatio.Xunit;

/// <summary>
/// Works just like [Fact] except that failures are retried (by default, 3 times).
/// </summary>
[XunitTestCaseDiscoverer(typeof(RetryFactDiscoverer))]
public class RetryFactAttribute : FactAttribute
{
    public RetryFactAttribute(int maxRetries = 3)
    {
        MaxRetries = maxRetries < 1 ? 3 : maxRetries;
    }

    /// <summary>
    /// Number of retries allowed for a failed test. If unset (or set less than 1), will
    /// default to 3 attempts.
    /// </summary>
    public int MaxRetries { get; set; }
}
