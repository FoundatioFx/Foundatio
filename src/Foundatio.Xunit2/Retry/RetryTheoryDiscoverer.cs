using System.Collections.Generic;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Foundatio.Xunit2;

public class RetryTheoryDiscoverer : IXunitTestCaseDiscoverer
{
    readonly IMessageSink diagnosticMessageSink;

    public RetryTheoryDiscoverer(IMessageSink diagnosticMessageSink)
    {
        this.diagnosticMessageSink = diagnosticMessageSink;
    }

    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        var maxRetries = factAttribute.GetNamedArgument<int>("MaxRetries");
        if (maxRetries < 1)
            maxRetries = 3;

        yield return new RetryTheoryTestCase(diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod, maxRetries);
    }
}
