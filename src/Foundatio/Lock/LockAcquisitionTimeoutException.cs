using System;

namespace Foundatio.Lock;

/// <summary>
/// Thrown by <see cref="LockProviderExtensions.AcquireAsync(ILockProvider, string, TimeSpan?, TimeSpan?)"/>
/// (and related overloads) when a lock cannot be acquired before the requested timeout
/// elapses or the supplied <see cref="System.Threading.CancellationToken"/> is cancelled.
/// </summary>
/// <remarks>
/// Callers that treat lock unavailability as a normal control-flow outcome (best-effort
/// dedupe, opportunistic work) should call <c>TryAcquireAsync</c> instead and check the
/// returned <see cref="ILock"/> for <c>null</c>.
/// </remarks>
public sealed class LockAcquisitionTimeoutException : Exception
{
    public LockAcquisitionTimeoutException(string resource)
        : base($"Failed to acquire lock for resource '{resource}' before the timeout elapsed.")
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
    /// The resource identifier that the caller attempted to lock.
    /// </summary>
    public string Resource { get; }
}
