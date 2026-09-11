using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;

namespace Foundatio.Aws.Tests;

/// <summary>
/// Runs the shared transport conformance suite against AWS SQS/SNS. Set the environment variable
/// <c>FOUNDATIO_AWS_CONNECTION_STRING</c> (e.g. <c>serviceurl=http://localhost:4566;accesskey=test;secretkey=test;region=us-east-1</c>
/// for LocalStack, or real AWS credentials) to run; when it is not set every test is skipped. Inherits every base
/// [Fact], so a new conformance check runs against SQS/SNS automatically; capabilities SQS/SNS do not support
/// (priority, per-message expiration, push delivery, transport-native dead-letter) self-skip via their
/// <c>ISupports*</c> capability checks in the base suite.
/// </summary>
public class AwsMessageTransportConformanceTests : MessageTransportConformanceTests
{
    // One prefix per test run isolates these queues/topics from prior runs and other environments on the same broker.
    private static readonly string RunPrefix = "fnd-conf-" + Guid.NewGuid().ToString("N")[..8] + "-";

    public AwsMessageTransportConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override IMessageTransport? CreateTransport()
    {
        string? connectionString = Environment.GetEnvironmentVariable("FOUNDATIO_AWS_CONNECTION_STRING");
        if (String.IsNullOrEmpty(connectionString))
            return null; // not configured -> the base suite skips every test

        var options = AwsMessageTransportOptions.FromConnectionString(connectionString);
        options.ResourcePrefix = RunPrefix;
        return new AwsMessageTransport(options);
    }

    [Fact]
    public override Task CompleteAsync_WithExpiredReceipt_ThrowsReceiptExpiredExceptionAsync()
    {
        // Explicit, visible opt-out (not a silent skip): SQS DeleteMessage with a stale/used receipt handle is
        // idempotent and does not raise. Strict receipt validation is transport-specific, not part of the shared
        // contract, so SQS does not satisfy this check.
        Assert.Skip("SQS DeleteMessage is idempotent for a stale receipt handle; strict receipt validation is not part of the shared contract.");
        return Task.CompletedTask;
    }
}
