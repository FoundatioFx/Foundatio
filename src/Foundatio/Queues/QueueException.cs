using System;

namespace Foundatio.Queues;

/// <summary>
/// Exception thrown for queue operation errors.
/// </summary>
public class QueueException : Exception
{
    public QueueException(string message) : base(message)
    {
    }

    public QueueException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
