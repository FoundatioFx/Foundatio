using System;

namespace Foundatio.Storage;

public class StorageException : Exception
{
    public StorageException(string message) : base(message)
    {
    }

    public StorageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
