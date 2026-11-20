using System;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// The delivery count and retry time of a record released in place (a grouped retry), which only the receiver holding
/// it knows. <see cref="MessageId"/> confirms the record at the offset is still the same one.
/// </summary>
internal sealed record KafkaReceiverAttempt(string MessageId, int DeliveryCount, DateTimeOffset? AvailableAt);
