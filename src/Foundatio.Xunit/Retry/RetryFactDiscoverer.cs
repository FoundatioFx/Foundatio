#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit.Sdk;
using Xunit.v3;

namespace Foundatio.Xunit;

public class RetryFactDiscoverer : IXunitTestCaseDiscoverer
{
    public ValueTask<IReadOnlyCollection<IXunitTestCase>> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        var maxRetries = 3;
        if (factAttribute is RetryFactAttribute retryAttribute)
            maxRetries = retryAttribute.MaxRetries < 1 ? 3 : retryAttribute.MaxRetries;

        var testCase = new RetryTestCase(
            testMethod,
            testCaseDisplayName: factAttribute.DisplayName ?? testMethod.MethodName,
            uniqueID: $"{testMethod.TestClass.TestCollection.TestAssembly.Assembly.FullName}:{testMethod.TestClass.Class.Name}:{testMethod.MethodName}",
            @explicit: factAttribute.Explicit,
            skipExceptions: null,
            skipReason: factAttribute.Skip,
            skipType: factAttribute.SkipType,
            skipUnless: factAttribute.SkipUnless,
            skipWhen: factAttribute.SkipWhen,
            traits: null,
            testMethodArguments: null,
            sourceFilePath: null,
            sourceLineNumber: null,
            timeout: factAttribute.Timeout > 0 ? factAttribute.Timeout : null,
            maxRetries: maxRetries);

        return new ValueTask<IReadOnlyCollection<IXunitTestCase>>([testCase]);
    }
}
