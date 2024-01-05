using System;
using Foundatio.Storage;
using Microsoft.Azure.Storage.Blob;

namespace Foundatio.Azure.Extensions
{
    public static class StorageExtensions
    {
        public static FileSpec ToFileInfo(this CloudBlockBlob blob)
        {
            if (blob.Properties.Length == -1)
                return null;

            return new FileSpec
            {
                Path = blob.Name,
                Size = blob.Properties.Length,
                Created = blob.Properties.LastModified?.UtcDateTime ?? DateTime.MinValue,
                Modified = blob.Properties.LastModified?.UtcDateTime ?? DateTime.MinValue
            };
        }
    }
}
