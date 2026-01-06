#nullable enable

using Xunit;
using Xunit.v3;

namespace Foundatio.Xunit;

/// <summary>
/// Works just like [Theory] except that failures are retried (by default, 3 times).
/// </summary>
[XunitTestCaseDiscoverer(typeof(RetryTheoryDiscoverer))]
public class RetryTheoryAttribute : TheoryAttribute
{
    public RetryTheoryAttribute(int maxRetries = 3)
    {
        MaxRetries = maxRetries < 1 ? 3 : maxRetries;
    }

    /// <summary>
    /// Number of retries allowed for a failed test. If unset (or set less than 1), will
    /// default to 3 attempts.
    /// </summary>
    public int MaxRetries { get; set; }
}
