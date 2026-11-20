namespace Foundatio.Messaging.Transport;

/// <summary>What a receiver did with a record that <see cref="KafkaCatchUpReader"/> read again.</summary>
internal enum KafkaReadAgainResult
{
    /// <summary>The record is not the first skipped record of its group, so it is not needed.</summary>
    NotNeeded,

    /// <summary>The record was the first skipped record of its group, and the receiver kept it.</summary>
    Kept,

    /// <summary>The record is the first skipped record of its group, but the group has no room for it yet.</summary>
    NoRoom,

    /// <summary>
    /// Nothing more is read again from the record's partition: the receiver no longer owns it, is giving it up or
    /// loading its state, or has no skipped records left on it.
    /// </summary>
    Gone
}
