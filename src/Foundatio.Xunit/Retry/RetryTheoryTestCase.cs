#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Sdk;
using Xunit.v3;

namespace Foundatio.Xunit;

public class RetryTheoryTestCase : XunitDelayEnumeratedTheoryTestCase, ISelfExecutingXunitTestCase
{
    private int _maxRetries;

    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public RetryTheoryTestCase()
    { }

    public RetryTheoryTestCase(
        IXunitTestMethod testMethod,
        string testCaseDisplayName,
        string uniqueID,
        bool @explicit,
        bool skipTestWithoutData,
        Type[]? skipExceptions,
        string? skipReason,
        Type? skipType,
        string? skipUnless,
        string? skipWhen,
        Dictionary<string, HashSet<string>>? traits,
        string? sourceFilePath,
        int? sourceLineNumber,
        int? timeout,
        int maxRetries)
        : base(
            testMethod,
            testCaseDisplayName,
            uniqueID,
            @explicit,
            skipTestWithoutData,
            skipExceptions,
            skipReason,
            skipType,
            skipUnless,
            skipWhen,
            traits,
            sourceFilePath,
            sourceLineNumber,
            timeout)
    {
        _maxRetries = maxRetries;
    }

    public int MaxRetries => _maxRetries;

    public async ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
    {
        var runCount = 0;

        while (true)
        {
            // Capture and delay messages until we know we've decided to accept the final result
            using var delayedMessageBus = new DelayedMessageBus(messageBus);
            var testAggregator = new ExceptionAggregator();

            var tests = await CreateTests();
            var summary = await XunitTestCaseRunner.Instance.Run(
                this,
                tests,
                delayedMessageBus,
                testAggregator,
                cancellationTokenSource,
                TestMethod.TestClass.Class.Name,
                TestMethod.TestClass.Class.Name,
                explicitOption,
                constructorArguments);

            if (testAggregator.HasExceptions || summary.Failed == 0 || ++runCount >= _maxRetries)
            {
                aggregator.Aggregate(testAggregator);
                delayedMessageBus.Flush();
                return summary;
            }

            // Retry silently - messages are discarded when delayedMessageBus is disposed
        }
    }

    protected override void Serialize(IXunitSerializationInfo info)
    {
        base.Serialize(info);
        info.AddValue("MaxRetries", _maxRetries);
    }

    protected override void Deserialize(IXunitSerializationInfo info)
    {
        base.Deserialize(info);
        _maxRetries = info.GetValue<int>("MaxRetries");
    }
}
