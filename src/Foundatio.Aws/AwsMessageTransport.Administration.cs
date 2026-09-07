using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS.Model;

namespace Foundatio.Messaging;

public sealed partial class AwsMessageTransport
{
    /// <summary>Validates existence and declared SQS attributes, reporting all mismatches together.</summary>
    public async Task ValidateAsync(IReadOnlyList<DestinationDeclaration> declarations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var problems = new List<string>();
        foreach (var declaration in declarations)
        {
            if (!await ExistsAsync(declaration.Address, cancellationToken).ConfigureAwait(false))
            {
                problems.Add($"Destination '{declaration.Address.Key}' does not exist.");
                continue;
            }
            if (declaration.Address.Role != DestinationRole.Queue) continue;
            ValidateQueueArguments(declaration.ProviderArguments);
            string url = await ResolveQueueUrlAsync(declaration.Address, cancellationToken).ConfigureAwait(false);
            var response = await _sqs.Value.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["All"]
            }, cancellationToken).ConfigureAwait(false);
            foreach (var expected in declaration.ProviderArguments ?? new Dictionary<string, string>())
            {
                response.Attributes.TryGetValue(expected.Key, out var actual);
                if (!String.Equals(expected.Value, actual, StringComparison.Ordinal))
                    problems.Add($"Queue '{declaration.Address.Name}' attribute {expected.Key} is '{actual ?? "(unset)"}', expected '{expected.Value}'.");
            }
            if (response.Attributes.TryGetValue("RedrivePolicy", out string? redrive) && !String.IsNullOrEmpty(redrive))
                problems.Add($"Queue '{declaration.Address.Name}' has a broker RedrivePolicy. Foundatio owns delivery attempts; remove the broker policy.");
        }
        if (problems.Count > 0) throw new InvalidOperationException(String.Join(Environment.NewLine, problems));
    }

    private static void ValidateQueueArguments(IReadOnlyDictionary<string, string>? arguments)
    {
        if (arguments is null) return;
        if (arguments.TryGetValue("RedrivePolicy", out var policy) && !String.IsNullOrEmpty(policy))
            throw new ArgumentException("Foundatio owns retry and dead-letter policy; do not configure a broker RedrivePolicy.", nameof(arguments));
        foreach (var argument in arguments)
        {
            if (argument.Key is not ("VisibilityTimeout" or "MessageRetentionPeriod")) continue;
            int min = argument.Key == "VisibilityTimeout" ? 1 : 60;
            int max = argument.Key == "VisibilityTimeout" ? 43200 : 1209600;
            if (!Int32.TryParse(argument.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
                throw new ArgumentException($"SQS {argument.Key} must be between {min} and {max} seconds.", nameof(arguments));
        }
    }

}
