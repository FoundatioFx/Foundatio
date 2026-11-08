using System;

namespace Foundatio.Jobs;

/// <summary>A job reported failure through <see cref="JobResult"/> without an exception.</summary>
public sealed class JobFailedException : Exception
{
    public JobFailedException(string message) : base(message)
    {
    }
}
