using System;
using Amazon;
using Amazon.Runtime;
using Foundatio.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio;

public static class AwsFoundatioBuilderExtensions
{
    /// <summary>
    /// Runs messaging (queues on SQS, pub/sub on SNS+SQS) over AWS. With no arguments it relies on the AWS SDK's default
    /// region and credential resolution; common settings (ServiceUrl, Region, ResourcePrefix, AccessKey/SecretKey) are
    /// also bound from an "Aws" configuration section when present, and <paramref name="configure"/> can override
    /// anything. Point ServiceUrl at LocalStack to run without a cloud account.
    /// </summary>
    public static FoundatioBuilder UseAws(this FoundatioBuilder.MessagingBuilder builder, Action<AwsMessageTransportOptions>? configure = null)
    {
        return builder.UseTransport(sp =>
        {
            var options = new AwsMessageTransportOptions();
            BindFromConfiguration(options, sp.GetService<IConfiguration>()?.GetSection("Aws"));
            configure?.Invoke(options);
            return new AwsMessageTransport(options);
        });
    }

    private static void BindFromConfiguration(AwsMessageTransportOptions options, IConfiguration? section)
    {
        if (section is null)
            return;

        if (section["ServiceUrl"] is { Length: > 0 } serviceUrl)
            options.ServiceUrl = serviceUrl;

        if (section["Region"] is { Length: > 0 } region)
            options.Region = RegionEndpoint.GetBySystemName(region);

        if (section["ResourcePrefix"] is { Length: > 0 } prefix)
            options.ResourcePrefix = prefix;

        if (section["AccessKey"] is { Length: > 0 } accessKey && section["SecretKey"] is { Length: > 0 } secretKey)
            options.Credentials = new BasicAWSCredentials(accessKey, secretKey);
    }
}
