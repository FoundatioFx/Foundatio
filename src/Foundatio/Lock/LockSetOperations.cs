using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Lock;

/// <summary>
/// Runs an operation against every lock in a set. Every operation is attempted, even when one throws synchronously.
/// When operations fail, the first <see cref="LockException"/> is rethrown, because a lost lease is definite while a
/// provider error may be transient; otherwise the first error is rethrown. Errors that are not rethrown are logged.
/// </summary>
internal static class LockSetOperations
{
    public static Task RenewAllAsync(IEnumerable<ILock> locks, TimeSpan? timeUntilExpires, ILogger logger)
    {
        return WhenAllAsync(locks.Select(async l => await l.RenewAsync(timeUntilExpires).AnyContext()), logger);
    }

    public static Task ReleaseAllAsync(IEnumerable<ILock> locks, ILogger logger)
    {
        return WhenAllAsync(locks.Select(async l => await l.ReleaseAsync().AnyContext()), logger);
    }

    private static async Task WhenAllAsync(IEnumerable<Task> operations, ILogger logger)
    {
        var task = Task.WhenAll(operations);
        try
        {
            await task.AnyContext();
        }
        catch (Exception) when (task.Exception is { } aggregate)
        {
            var errors = aggregate.InnerExceptions;
            var error = errors.FirstOrDefault(e => e is LockException) ?? errors[0];
            foreach (var dropped in errors.Where(e => !ReferenceEquals(e, error)))
                logger.LogError(dropped, "Lock operation failed alongside {ErrorType}: {Message}", error.GetType().Name, dropped.Message);

            ExceptionDispatchInfo.Throw(error);
        }
    }
}
