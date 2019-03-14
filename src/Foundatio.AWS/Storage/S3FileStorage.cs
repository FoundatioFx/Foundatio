using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Foundatio.AWS.Extensions;
using Foundatio.Extensions;
using Foundatio.Serializer;

namespace Foundatio.Storage {
    public class S3FileStorage : IFileStorage {
        private readonly string _bucket;
        private readonly ISerializer _serializer;
        private readonly AmazonS3Client _client;
        private readonly bool _useChunkEncoding;

        public S3FileStorage(S3FileStorageOptions options) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _bucket = options.Bucket;
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            _useChunkEncoding = options.UseChunkEncoding ?? true;

            var credentials = options.Credentials ?? FallbackCredentialsFactory.GetCredentials();

            if (string.IsNullOrEmpty(options.ServiceUrl)) {
                var region = options.Region ?? FallbackRegionFactory.GetRegionEndpoint();
                _client = new AmazonS3Client(credentials, region);
            } else {
                _client = new AmazonS3Client(
                    credentials,
                    new AmazonS3Config {
                        ServiceURL = options.ServiceUrl
                    });
            }
        }

        public S3FileStorage(Builder<S3FileStorageOptionsBuilder, S3FileStorageOptions> builder)
            : this(builder(new S3FileStorageOptionsBuilder()).Build()) { }

        ISerializer IHaveSerializer.Serializer => _serializer;

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var req = new GetObjectRequest {
                BucketName = _bucket,
                Key = path.Replace('\\', '/')
            };

            var res = await _client.GetObjectAsync(req, cancellationToken).AnyContext();
            if (!res.HttpStatusCode.IsSuccessful())
                return null;

            return new ActionableStream(res.ResponseStream, () => {
                res?.Dispose();
            });
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var req = new GetObjectMetadataRequest {
                BucketName = _bucket,
                Key = path.Replace('\\', '/')
            };

            try {
                var res = await _client.GetObjectMetadataAsync(req).AnyContext();

                if (!res.HttpStatusCode.IsSuccessful())
                    return null;

                return new FileSpec {
                    Size = res.ContentLength,
                    Created = res.LastModified.ToUniversalTime(),  // TODO: Need to fix this
                    Modified = res.LastModified.ToUniversalTime(),
                    Path = path
                };
            } catch (AmazonS3Exception) {
                return null;
            }
        }

        public async Task<bool> ExistsAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var result = await GetFileInfoAsync(path).AnyContext();
            return result != null;
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var req = new PutObjectRequest {
                BucketName = _bucket,
                Key = path.Replace('\\', '/'),
                AutoResetStreamPosition = false,
                AutoCloseStream = !stream.CanSeek,
                InputStream = stream.CanSeek ? stream : AmazonS3Util.MakeStreamSeekable(stream),
                UseChunkEncoding = _useChunkEncoding
            };

            var res = await _client.PutObjectAsync(req, cancellationToken).AnyContext();
            return res.HttpStatusCode.IsSuccessful();
        }

        public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            var req = new CopyObjectRequest {
                SourceBucket = _bucket,
                SourceKey = path.Replace('\\', '/'),
                DestinationBucket = _bucket,
                DestinationKey = newPath.Replace('\\', '/')
            };

            var res = await _client.CopyObjectAsync(req, cancellationToken).AnyContext();
            if (!res.HttpStatusCode.IsSuccessful())
                return false;

            var delReq = new DeleteObjectRequest {
                BucketName = _bucket,
                Key = path.Replace('\\', '/')
            };

            var delRes = await _client.DeleteObjectAsync(delReq, cancellationToken).AnyContext();
            return delRes.HttpStatusCode.IsSuccessful();
        }

        public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            var req = new CopyObjectRequest {
                SourceBucket = _bucket,
                SourceKey = path.Replace('\\', '/'),
                DestinationBucket = _bucket,
                DestinationKey = targetPath.Replace('\\', '/')
            };

            var res = await _client.CopyObjectAsync(req, cancellationToken).AnyContext();
            return res.HttpStatusCode.IsSuccessful();
        }

        public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var req = new DeleteObjectRequest {
                BucketName = _bucket,
                Key = path.Replace('\\', '/')
            };

            var res = await _client.DeleteObjectAsync(req, cancellationToken).AnyContext();
            return res.HttpStatusCode.IsSuccessful();
        }

        public async Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = new CancellationToken()) {
            var criteria = GetRequestCriteria(searchPattern);
            int count = 0;
            const int PAGE_SIZE = 100;

            var listRequest = new ListObjectsV2Request { BucketName = _bucket, Prefix = criteria.Prefix, MaxKeys = PAGE_SIZE };
            var deleteRequest = new DeleteObjectsRequest { BucketName = _bucket };
            var errors = new List<DeleteError>();

            ListObjectsV2Response listResponse;
            do
            {
                listResponse = await _client.ListObjectsV2Async(listRequest, cancellationToken).AnyContext();

                var keys = listResponse.S3Objects.MatchesPattern(criteria.Pattern).Select(o => new KeyVersion { Key = o.Key });
                deleteRequest.Objects.AddRange(keys);

                var deleteResponse = await _client.DeleteObjectsAsync(deleteRequest, cancellationToken).AnyContext();
                if (deleteResponse.DeleteErrors.Count > 0) {
                    // retry 1 time, continue on.
                    var deleteRetryRequest = new DeleteObjectsRequest { BucketName = _bucket };
                    deleteRetryRequest.Objects.AddRange(deleteResponse.DeleteErrors.Select(e => new KeyVersion { Key = e.Key }));
                    var deleteRetryResponse = await _client.DeleteObjectsAsync(deleteRetryRequest, cancellationToken).AnyContext();
                    if (deleteRetryResponse.DeleteErrors.Count > 0)
                        errors.AddRange(deleteRetryResponse.DeleteErrors);
                }

                count += deleteResponse.DeletedObjects.Count;
                deleteRequest.Objects.Clear();

                listRequest.ContinuationToken = listResponse.NextContinuationToken;
            } while (listResponse.IsTruncated);

            if (errors.Count > 0) {
                int more = errors.Count > 20 ? errors.Count - 20 : 0;
                throw new Exception($"Unable to delete all S3 entries \"{String.Join(",", errors.Take(20).Select(e => e.Key))}\"{(more > 0 ? $" plus {more} more" : "")}.");
            }

            return count;
        }

        public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default) {
            if (pageSize <= 0)
                return PagedFileListResult.Empty;

            var criteria = GetRequestCriteria(searchPattern);

            var result = new PagedFileListResult(r => GetFiles(criteria, pageSize, cancellationToken));
            await result.NextPageAsync().AnyContext();

            return result;
        }

        private async Task<NextPageResult> GetFiles(SearchCriteria criteria, int pageSize, CancellationToken cancellationToken, string continuationToken = null) {
            var req = new ListObjectsV2Request {
                BucketName = _bucket,
                MaxKeys = pageSize,
                Prefix = criteria.Prefix,
                ContinuationToken = continuationToken
            };

            var response = await _client.ListObjectsV2Async(req, cancellationToken).AnyContext();
            return new NextPageResult {
                Success = response.HttpStatusCode.IsSuccessful(),
                HasMore = response.IsTruncated,
                Files = response.S3Objects.MatchesPattern(criteria.Pattern).Select(blob => blob.ToFileInfo()).ToList(),
                NextPageFunc = response.IsTruncated ? r => GetFiles(criteria, pageSize, cancellationToken, response.NextContinuationToken) : (Func<PagedFileListResult, Task<NextPageResult>>)null
            };
    }

    private class SearchCriteria {
            public string Prefix { get; set; }
            public Regex Pattern { get; set; }
        }

        private SearchCriteria GetRequestCriteria(string searchPattern) {
            Regex patternRegex = null;
            searchPattern = searchPattern?.Replace('\\', '/');

            string prefix = searchPattern;
            int wildcardPos = searchPattern?.IndexOf('*') ?? -1;
            if (searchPattern != null && wildcardPos >= 0) {
                patternRegex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");
                int slashPos = searchPattern.LastIndexOf('/');
                prefix = slashPos >= 0 ? searchPattern.Substring(0, slashPos) : String.Empty;
            }

            return new SearchCriteria {
                Prefix = prefix ?? String.Empty,
                Pattern = patternRegex
            };
        }

        public void Dispose() {
            _client?.Dispose();
        }
    }
}
