namespace Foundatio.Messaging;

/// <summary>
/// The ids and group key of one message in a batch, returned by the selector passed to
/// <see cref="IMessageClient.SendBatchAsync{T}(System.Collections.Generic.IEnumerable{T}, System.Func{T, MessageKeys}, SendOptions?, System.Threading.CancellationToken)"/>
/// or <c>PublishBatchAsync</c>. Each is up to 128 characters; see <see cref="DeliveryOptions"/> for what they do.
/// </summary>
/// <example>
/// <code>
/// // Ids derived from the messages make a retried batch safe: the transport drops the copies it already has.
/// await messages.SendBatchAsync(orders, o => new MessageKeys { MessageId = $"order-{o.Id}", DeduplicationId = $"order-{o.Id}", GroupKey = o.AccountId });
/// </code>
/// </example>
public readonly record struct MessageKeys
{
    /// <summary>The message id. Generated when null.</summary>
    public string? MessageId { get; init; }

    /// <summary>The <see cref="DeliveryOptions.DeduplicationId"/>, or null for none.</summary>
    public string? DeduplicationId { get; init; }

    /// <summary>The <see cref="DeliveryOptions.GroupKey"/>. When null, the batch options' group key is used.</summary>
    public string? GroupKey { get; init; }
}
