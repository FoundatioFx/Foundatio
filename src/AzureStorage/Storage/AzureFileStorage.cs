using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Foundatio.Azure.Extensions;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Foundatio.Storage {
    public class AzureFileStorage : IFileStorage {
        private readonly CloudBlobContainer _container;

        public AzureFileStorage(string connectionString, string containerName = "ex-events") {
            var account = CloudStorageAccount.Parse(connectionString);
            var client = account.CreateCloudBlobClient();
            _container = client.GetContainerReference(containerName);
            _container.CreateIfNotExists();
        }

        public string GetFileContents(string path) {
            var blockBlob = _container.GetBlockBlobReference(path);
            try {
                return blockBlob.DownloadText();
            } catch (StorageException ex) {
                if (ex.RequestInformation.HttpStatusCode == 404)
                    return null;

                throw;
            }
        }

        public FileSpec GetFileInfo(string path) {
            var blob = _container.GetBlockBlobReference(path);
            return blob.ToFileInfo();
        }

        public bool Exists(string path) {
            var blockBlob = _container.GetBlockBlobReference(path);
            return blockBlob.Exists();
        }

        public bool SaveFile(string path, string contents) {
            var blockBlob = _container.GetBlockBlobReference(path);
            blockBlob.UploadText(contents);

            return true;
        }

        public bool RenameFile(string oldpath, string newpath) {
            var oldBlob = _container.GetBlockBlobReference(oldpath);
            var newBlob = _container.GetBlockBlobReference(newpath);
            
            using (var stream = new MemoryStream())
            {
                oldBlob.DownloadToStream(stream);
                stream.Seek(0, SeekOrigin.Begin);
                newBlob.UploadFromStream(stream);

                oldBlob.Delete();
            }

            return true;
        }

        public bool DeleteFile(string path) {
            var blockBlob = _container.GetBlockBlobReference(path);
            return blockBlob.DeleteIfExists();
        }

        public IEnumerable<FileSpec> GetFileList(string searchPattern = null, int? limit = null) {
            // TODO: ListBlobs only takes a blob name prefix. As such we currently do not support wildcards (E.G., q\event*.json).
            if (searchPattern != null)
                searchPattern = searchPattern.TrimEnd('*', '\\');

            var blobItems = _container.ListBlobs(searchPattern, true);
            if (limit.HasValue)
                blobItems = blobItems.Take(limit.Value);

            return blobItems.OfType<CloudBlockBlob>().Select(blob => blob.ToFileInfo());
        }

        public void Dispose() {}
    }
}
