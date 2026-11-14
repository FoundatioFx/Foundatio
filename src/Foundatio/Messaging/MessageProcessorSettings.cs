using System;

namespace Foundatio.Messaging;

/// <summary>How a <see cref="MessageProcessor"/> receives.</summary>
internal sealed record MessageProcessorSettings
{
    /// <summary>How many deliveries are handled at once.</summary>
    public required int MaxConcurrency { get; init; }

    /// <summary>The requested lease; capped at the transport's <c>MaxLeaseDuration</c>.</summary>
    public required TimeSpan LeaseDuration { get; init; }

    /// <summary>How long a failed receiver's in-flight deliveries may finish before it is replaced.</summary>
    public required TimeSpan ReplaceTimeout { get; init; }

    /// <summary>What handles the deliveries, for the log.</summary>
    public required string Description { get; init; }
}
