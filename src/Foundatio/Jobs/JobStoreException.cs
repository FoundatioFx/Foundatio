using System;

namespace Foundatio.Jobs;

/// <summary>
/// Thrown when a job store operation fails because of the database behind it: it is unreachable, timed out, or
/// rejected the command. The database client's exception is the <see cref="Exception.InnerException"/>.
/// </summary>
/// <remarks>
/// Every job store throws it for such failures, so code calling <see cref="IJobClient"/> catches one type whichever
/// store is configured. Invalid arguments still throw <see cref="ArgumentException"/>.
/// </remarks>
public class JobStoreException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public JobStoreException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the database client's exception.</summary>
    public JobStoreException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
