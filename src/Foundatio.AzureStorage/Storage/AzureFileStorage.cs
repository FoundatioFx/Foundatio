﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Azure.Extensions;
using Foundatio.Extensions;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Foundatio.Storage {
    public class AzureFileStorage : IFileStorage {
        private readonly CloudBlobContainer _container;

        public AzureFileStorage(string connectionString, string containerName = "storage") {
            var account = CloudStorageAccount.Parse(connectionString);
            var client = account.CreateCloudBlobClient();
            _container = client.GetContainerReference(containerName);
            _container.CreateIfNotExists();
        }

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            var blockBlob = _container.GetBlockBlobReference(path);
            try {
                return await blockBlob.OpenReadAsync(cancellationToken).AnyContext();
            } catch (StorageException ex) {
                if (ex.RequestInformation.HttpStatusCode == 404)
                    return null;

                throw;
            }
        }

        public Task<FileSpec> GetFileInfoAsync(string path) {
            var blob = _container.GetBlockBlobReference(path);
            return Task.FromResult(blob.ToFileInfo());
        }

        public async Task<bool> ExistsAsync(string path) {
            var blockBlob = _container.GetBlockBlobReference(path);
            return await blockBlob.ExistsAsync().AnyContext();
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken)) {
            var blockBlob = _container.GetBlockBlobReference(path);
            await blockBlob.UploadFromStreamAsync(stream, cancellationToken).AnyContext();

            return true;
        }

        public async Task<bool> RenameFileAsync(string oldpath, string newpath, CancellationToken cancellationToken = default(CancellationToken)) {
            var oldBlob = _container.GetBlockBlobReference(oldpath);
            if (!(await CopyFileAsync(oldpath, newpath, cancellationToken).AnyContext()))
                return false;

            return await oldBlob.DeleteIfExistsAsync(cancellationToken).AnyContext();
        }

        public async Task<bool> CopyFileAsync(string path, string targetpath, CancellationToken cancellationToken = default(CancellationToken)) {
            var oldBlob = _container.GetBlockBlobReference(path);
            var newBlob = _container.GetBlockBlobReference(targetpath);

            using (var stream = new MemoryStream()) {
                await oldBlob.DownloadToStreamAsync(stream, cancellationToken).AnyContext();
                
                if (stream.CanSeek)
                    stream.Seek(0, SeekOrigin.Begin);

                await newBlob.UploadFromStreamAsync(stream, cancellationToken).AnyContext();
            }

            return true;
        }

        public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            var blockBlob = _container.GetBlockBlobReference(path);
            return await blockBlob.DeleteIfExistsAsync(cancellationToken).AnyContext();
        }

        public async Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (limit.HasValue && limit.Value <= 0)
                return new List<FileSpec>();
            
            searchPattern = searchPattern?.Replace('\\', '/');
            string prefix = searchPattern;
            Regex patternRegex = null;
            int wildcardPos = searchPattern?.IndexOf('*') ?? -1;
            if (searchPattern != null && wildcardPos >= 0) {
                patternRegex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");
                int slashPos = searchPattern.LastIndexOf('/');
                prefix = slashPos >= 0 ? searchPattern.Substring(0, slashPos) : String.Empty;
            }
            prefix = prefix ?? String.Empty;

            BlobContinuationToken continuationToken = null;
            var blobs = new List<CloudBlockBlob>();
            do {
                var listingResult = await _container.ListBlobsSegmentedAsync(prefix, true, BlobListingDetails.Metadata, limit, continuationToken, null, null, cancellationToken).AnyContext();
                continuationToken = listingResult.ContinuationToken;

                // TODO: Implement paging
                blobs.AddRange(listingResult.Results.OfType<CloudBlockBlob>().MatchesPattern(patternRegex));
            }
            while (continuationToken != null && blobs.Count < limit);

            if (limit.HasValue)
                blobs = blobs.Take(limit.Value).ToList();

            return blobs.Select(blob => blob.ToFileInfo());
        }

        public void Dispose() {}
    }

    internal static class BlobListExtensions {
        internal static IEnumerable<CloudBlockBlob> MatchesPattern(this IEnumerable<CloudBlockBlob> blobs, Regex patternRegex) {
            return blobs.Where(blob => patternRegex == null || patternRegex.IsMatch(blob.ToFileInfo().Path));
        }
    }
}
