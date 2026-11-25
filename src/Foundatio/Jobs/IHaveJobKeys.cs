namespace Foundatio.Jobs;

/// <summary>
/// Job arguments that choose their job's <see cref="EnqueueOptions.ConcurrencyKey"/> and
/// <see cref="EnqueueOptions.DeduplicationKey"/>, so every enqueue of them gets the keys, in every process and from CRON
/// schedules too, without passing <see cref="EnqueueOptions"/>. A key set on <see cref="EnqueueOptions"/> wins over the
/// arguments' key. Return null for no key.
/// </summary>
/// <remarks>
/// Implement the members explicitly, as below, so they are not serialized with the arguments. The keys are read when
/// the job is enqueued and stored with it; changing them later does not change jobs already enqueued.
/// </remarks>
/// <example>
/// <code>
/// public record ResetProjectData(string ProjectId) : IHaveJobKeys
/// {
///     // One reset per project runs at a time, and a reset requested while another waits joins it.
///     string? IHaveJobKeys.ConcurrencyKey => ProjectId;
///     string? IHaveJobKeys.DeduplicationKey => ProjectId;
/// }
/// </code>
/// </example>
public interface IHaveJobKeys
{
    /// <summary>The <see cref="EnqueueOptions.ConcurrencyKey"/> of the job these arguments are enqueued as, or null for none.</summary>
    string? ConcurrencyKey => null;

    /// <summary>The <see cref="EnqueueOptions.DeduplicationKey"/> of the job these arguments are enqueued as, or null for none.</summary>
    string? DeduplicationKey => null;
}
