using System;

namespace Foundatio.Lock;

public class LockException : Exception
{
    public LockException(string message) : base(message)
    {
    }

    public LockException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
