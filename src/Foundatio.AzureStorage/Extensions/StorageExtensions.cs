﻿using System;
using Foundatio.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Foundatio.Azure.Extensions {
    public static class StorageExtensions {
        public static FileSpec ToFileInfo(this CloudBlockBlob blob) {
            if (blob.Properties.Length == -1)
                return null;

            return new FileSpec {
                Path = blob.Name,
                Size = blob.Properties.Length,
                Modified = blob.Properties.LastModified.HasValue ? blob.Properties.LastModified.Value.UtcDateTime : DateTime.MinValue,
                Created = blob.Properties.LastModified.HasValue ? blob.Properties.LastModified.Value.UtcDateTime : DateTime.MinValue
            };
        }
    }
}
