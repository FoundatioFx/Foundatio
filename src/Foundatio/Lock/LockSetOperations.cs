using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Lock;

/// <summary>
/// Runs an operation against every lock in a set. Every operation is attempted, even when one throws synchronously.
/// When operations fail, provider errors take precedence over <see cref="LockException"/> so infrastructure failures
/// are not masked by a lost lease; otherwise the first <see cref="LockException"/> is rethrown.
/// </summary>
internal static class LockSetOperations
{
    public static Task RenewAllAsync(IEnumerable<ILock> locks, TimeSpan? timeUntilExpires)
    {
        return WhenAllAsync(locks.Select(async l => await l.RenewAsync(timeUntilExpires).AnyContext()));
    }

    public static Task ReleaseAllAsync(IEnumerable<ILock> locks)
    {
        return WhenAllAsync(locks.Select(async l => await l.ReleaseAsync().AnyContext()));
    }

    private static async Task WhenAllAsync(IEnumerable<Task> operations)
    {
        var task = Task.WhenAll(operations);
        try
        {
            await task.AnyContext();
        }
        catch (Exception) when (task.Exception is { } aggregate)
        {
            var errors = aggregate.InnerExceptions;
            ExceptionDispatchInfo.Throw(errors.FirstOrDefault(e => e is not LockException) ?? errors[0]);
        }
    }
}
