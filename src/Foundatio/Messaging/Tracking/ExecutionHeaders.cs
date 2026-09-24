namespace Foundatio.Messaging;

/// <summary>Optional execution metadata carried by ordinary Foundatio messages.</summary>
public static class ExecutionHeaders
{
    public const string ExecutionId = "message.execution.id";
    public const string OriginalExecutionId = "message.execution.original_id";
    public const string EnqueuedAt = "message.enqueued_at";
    public const string ReplayedAt = "message.replayed_at";
    public const string OriginNode = "message.origin_node";
}
