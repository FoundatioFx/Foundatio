using System;

namespace Foundatio.Lock;

/// <summary>
/// Thrown by <see cref="ILockProvider.AcquireAsync(string, TimeSpan?, bool, System.Threading.CancellationToken)"/>
/// (and the multi-resource extension overloads) when a lock cannot be acquired before the
/// requested timeout elapses or the supplied <see cref="System.Threading.CancellationToken"/> is cancelled.
/// </summary>
/// <remarks>
/// Callers that treat lock unavailability as a normal control-flow outcome (best-effort
/// dedupe, opportunistic work) should call <see cref="ILockProvider.TryAcquireAsync"/> instead
/// and check the returned <see cref="ILock"/> for <c>null</c>.
/// </remarks>
public sealed class LockAcquisitionTimeoutException : Exception
{
    public LockAcquisitionTimeoutException(string resource)
        : base($"Failed to acquire lock for resource '{resource}'.")
    {
        Resource = resource;
    }

    public LockAcquisitionTimeoutException(string resource, string message)
        : base(message)
    {
        Resource = resource;
    }

    public LockAcquisitionTimeoutException(string resource, string message, Exception innerException)
        : base(message, innerException)
    {
        Resource = resource;
    }

    /// <summary>
    /// The resource identifier (or comma-separated list, for multi-resource acquisition)
    /// that the caller attempted to lock.
    /// </summary>
    public string Resource { get; }
}
