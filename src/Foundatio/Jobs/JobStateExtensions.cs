namespace Foundatio.Jobs;

public static class JobStateExtensions
{
    /// <summary>True for <see cref="JobState.Succeeded"/>, <see cref="JobState.Failed"/> and <see cref="JobState.Cancelled"/>.</summary>
    public static bool IsTerminal(this JobState state) => state is JobState.Succeeded or JobState.Failed or JobState.Cancelled;
}
