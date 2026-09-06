namespace Foundatio.QuickstartSample;

public sealed class SampleActivity
{
    public TaskCompletionSource EventHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CommandHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CleanupRan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
