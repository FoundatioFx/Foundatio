using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>Atomic transition guarantees for optional broker-driven execution tracking.</summary>
public abstract class MessageExecutionStoreConformanceTests
{
    protected abstract IMessageExecutionStore? CreateStore();
    private IMessageExecutionStore Store() { var store = CreateStore(); Assert.SkipWhen(store is null, "Execution store is not configured."); return store!; }
    private static MessageExecutionState Queued(string id) => new()
    {
        JobId = id,
        QueueName = "exports",
        MessageType = "Export",
        Status = MessageExecutionStatus.Queued,
        CreatedUtc = DateTimeOffset.UtcNow,
        LastUpdatedUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task ADeliveryAttempt_CanStartOnlyOnce()
    {
        var store = Store();
        string id = Guid.NewGuid().ToString("N");
        await store.SetJobStateAsync(Queued(id));
        Assert.True(await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Processing, attempt: 1, workerId: "first"));
        Assert.False(await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Processing, attempt: 1, workerId: "second"));
        Assert.Equal("first", (await store.GetJobStateAsync(id))!.WorkerId);
    }

    [Fact]
    public async Task OlderAttempt_CannotOverwriteANewerAttempt()
    {
        var store = Store();
        string id = Guid.NewGuid().ToString("N");
        await store.SetJobStateAsync(Queued(id));
        Assert.True(await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Processing, attempt: 1));
        Assert.True(await store.UpdateJobStatusAsync(id, MessageExecutionStatus.RetryPending, attempt: 1));
        Assert.True(await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Processing, attempt: 2));
        await store.UpdateJobProgressAsync(id, 40, "current", expectedAttempt: 2);
        await store.UpdateJobProgressAsync(id, 99, "stale", expectedAttempt: 1);
        Assert.False(await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Completed, attempt: 1));
        var state = await store.GetJobStateAsync(id);
        Assert.Equal(MessageExecutionStatus.Processing, state!.Status);
        Assert.Equal(40, state.Progress);
        Assert.Equal("current", state.ProgressMessage);
    }

    [Fact]
    public async Task RetryPending_IgnoresProgressFromTheFinishedAttempt()
    {
        var store = Store();
        string id = Guid.NewGuid().ToString("N");
        await store.SetJobStateAsync(Queued(id));
        await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Processing, attempt: 1);
        await store.UpdateJobStatusAsync(id, MessageExecutionStatus.RetryPending, attempt: 1);
        await store.UpdateJobProgressAsync(id, 80, "too late", expectedAttempt: 1);
        Assert.Equal(0, (await store.GetJobStateAsync(id))!.Progress);
    }

    [Fact]
    public async Task Cancellation_SurvivesProgressAndStatusWrites()
    {
        var store = Store();
        string id = Guid.NewGuid().ToString("N");
        await store.SetJobStateAsync(Queued(id));
        Assert.True(await store.RequestCancellationAsync(id));
        await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Processing, attempt: 1);
        await store.UpdateJobProgressAsync(id, 50, expectedAttempt: 1);
        Assert.True(await store.IsCancellationRequestedAsync(id));
        await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Cancelled, attempt: 1);
        Assert.False(await store.RequestCancellationAsync(id));
    }

    [Fact]
    public async Task TerminalState_RejectsDelayedSendReconciliation()
    {
        var store = Store();
        string id = Guid.NewGuid().ToString("N");
        await store.SetJobStateAsync(Queued(id));
        await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Processing, attempt: 1);
        await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Completed, attempt: 1, progress: 100);
        Assert.False(await store.UpdateJobStatusAsync(id, MessageExecutionStatus.EnqueueUnknown, errorMessage: "Late send timeout"));
        Assert.False(await store.UpdateJobStatusAsync(id, MessageExecutionStatus.Processing, attempt: 2));
        Assert.Equal(MessageExecutionStatus.Completed, (await store.GetJobStateAsync(id))!.Status);
    }
}
