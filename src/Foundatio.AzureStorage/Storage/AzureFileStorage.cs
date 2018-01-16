using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Azure.Extensions;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Foundatio.Storage {
    public class AzureFileStorage : IFileStorage {
        private readonly CloudBlobContainer _container;
        private readonly ISerializer _serializer;

        public AzureFileStorage(AzureFileStorageOptions options) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var account = CloudStorageAccount.Parse(options.ConnectionString);
            var client = account.CreateCloudBlobClient();
            _container = client.GetContainerReference(options.ContainerName);
            _container.CreateIfNotExistsAsync().GetAwaiter().GetResult();
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
        }

        ISerializer IHaveSerializer.Serializer => _serializer;

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var blockBlob = _container.GetBlockBlobReference(path);
            try {
                return await blockBlob.OpenReadAsync(null, null, null, cancellationToken).AnyContext();
            } catch (StorageException ex) {
                if (ex.RequestInformation.HttpStatusCode == 404)
                    return null;

                throw;
            }
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var blob = _container.GetBlockBlobReference(path);
            try {
                await blob.FetchAttributesAsync().AnyContext();
                return blob.ToFileInfo();
            } catch (Exception) { }

            return null;
        }

        public Task<bool> ExistsAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var blockBlob = _container.GetBlockBlobReference(path);
            return blockBlob.ExistsAsync();
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var blockBlob = _container.GetBlockBlobReference(path);
            await blockBlob.UploadFromStreamAsync(stream, null, null, null, cancellationToken).AnyContext();

            return true;
        }

        public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            var oldBlob = _container.GetBlockBlobReference(path);
            if (!(await CopyFileAsync(path, newPath, cancellationToken).AnyContext()))
                return false;

            return await oldBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, null, null, null, cancellationToken).AnyContext();
        }

        public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            var oldBlob = _container.GetBlockBlobReference(path);
            var newBlob = _container.GetBlockBlobReference(targetPath);

            await newBlob.StartCopyAsync(oldBlob, null, null, null, null, cancellationToken).AnyContext();
            while (newBlob.CopyState.Status == CopyStatus.Pending)
                await SystemClock.SleepAsync(50, cancellationToken).AnyContext();

            return newBlob.CopyState.Status == CopyStatus.Success;
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var blockBlob = _container.GetBlockBlobReference(path);
            return blockBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, null, null, null, cancellationToken);
        }

        public async Task DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = default(CancellationToken)) {
            var files = await GetFileListAsync(searchPattern, cancellationToken: cancellationToken).AnyContext();
            // TODO: We could batch this, but we should ensure the batch isn't thousands of files.
            foreach (var file in files)
                await DeleteFileAsync(file.Path).AnyContext();
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
            } while (continuationToken != null && blobs.Count < limit.GetValueOrDefault(Int32.MaxValue));

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
