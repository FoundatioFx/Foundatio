using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests.Jobs;

/// <summary>
/// Holds every lookup of expired jobs that a <see cref="Foundatio.Jobs.Store.RedisJobStore"/> on <see cref="Wrap"/>'s
/// connection makes, which only its sweeps do, until <see cref="Release"/> is called; then each one runs, or fails with
/// <see cref="Failure"/>.
/// </summary>
public sealed class SweepGate
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;

    /// <summary>The exception each lookup fails with once released, or null to run it.</summary>
    public Exception? Failure { get; init; }

    /// <summary>How many lookups have reached the gate.</summary>
    public int Held => Volatile.Read(ref _held);

    public void Release() => _released.TrySetResult();

    public IConnectionMultiplexer Wrap(IConnectionMultiplexer connection)
    {
        var proxy = DispatchProxy.Create<IConnectionMultiplexer, ConnectionProxy>();
        ((ConnectionProxy)(object)proxy).Initialize(connection, this);
        return proxy;
    }

    private async Task<RedisResult> HoldAsync(Func<Task<RedisResult>> evaluate)
    {
        Interlocked.Increment(ref _held);
        await _released.Task;
        if (Failure is not null)
            throw Failure;

        return await evaluate();
    }

    private static object? Forward(object target, MethodInfo method, object?[]? args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }

    public class ConnectionProxy : DispatchProxy
    {
        private IConnectionMultiplexer _inner = null!;
        private SweepGate _gate = null!;

        internal void Initialize(IConnectionMultiplexer inner, SweepGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result = Forward(_inner, targetMethod!, args);
            if (targetMethod!.Name != nameof(IConnectionMultiplexer.GetDatabase))
                return result;

            var proxy = Create<IDatabase, DatabaseProxy>();
            ((DatabaseProxy)(object)proxy).Initialize((IDatabase)result!, _gate);
            return proxy;
        }
    }

    public class DatabaseProxy : DispatchProxy
    {
        private IDatabase _inner = null!;
        private SweepGate _gate = null!;

        internal void Initialize(IDatabase inner, SweepGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IDatabaseAsync.ScriptEvaluateAsync) && args is [string, RedisKey[] { Length: 1 } keys, ..]
                && ((string)keys[0]!).EndsWith(":expiring", StringComparison.Ordinal))
                return _gate.HoldAsync(() => (Task<RedisResult>)Forward(_inner, targetMethod, args)!);

            return Forward(_inner, targetMethod, args);
        }
    }
}
