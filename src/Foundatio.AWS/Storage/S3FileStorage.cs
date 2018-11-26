using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Foundatio.AWS.Extensions;
using Foundatio.Extensions;
using Foundatio.Serializer;

namespace Foundatio.Storage {
    public class S3FileStorage : IFileStorage {
        private readonly AWSCredentials _credentials;
        private readonly RegionEndpoint _region;
        private readonly string _bucket;
        private readonly ISerializer _serializer;

        public S3FileStorage(S3FileStorageOptions options) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _credentials = options.Credentials ?? FallbackCredentialsFactory.GetCredentials();
            _region = options.Region ?? FallbackRegionFactory.GetRegionEndpoint();
            _bucket = options.Bucket;
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
        }

        public S3FileStorage(Builder<S3FileStorageOptionsBuilder, S3FileStorageOptions> builder)
            : this(builder(new S3FileStorageOptionsBuilder()).Build()) { }

        ISerializer IHaveSerializer.Serializer => _serializer;

        private AmazonS3Client CreateClient() {
            return new AmazonS3Client(_credentials, _region);
        }

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            var client = CreateClient();
            var req = new GetObjectRequest {
                BucketName = _bucket,
                Key = path.Replace('\\', '/')
            };

            var res = await client.GetObjectAsync(req, cancellationToken).AnyContext();
            if (!res.HttpStatusCode.IsSuccessful())
                return null;

            return new ActionableStream(res.ResponseStream, () => {
                res?.Dispose();
                client?.Dispose();
            });
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            using (var client = CreateClient()) {
                var req = new GetObjectMetadataRequest {
                    BucketName = _bucket,
                    Key = path.Replace('\\', '/')
                };

                try {
                    var res = await client.GetObjectMetadataAsync(req).AnyContext();

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

            using (var client = CreateClient()) {
                var req = new PutObjectRequest {
                    BucketName = _bucket,
                    Key = path.Replace('\\', '/'),
                    AutoResetStreamPosition = false,
                    AutoCloseStream = !stream.CanSeek,
                    InputStream = stream.CanSeek ? stream : AmazonS3Util.MakeStreamSeekable(stream)
                };

                var res = await client.PutObjectAsync(req, cancellationToken).AnyContext();
                return res.HttpStatusCode.IsSuccessful();
            }
        }

        public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            using (var client = CreateClient()) {
                var req = new CopyObjectRequest {
                    SourceBucket = _bucket,
                    SourceKey = path.Replace('\\', '/'),
                    DestinationBucket = _bucket,
                    DestinationKey = newPath.Replace('\\', '/')
                };

                var res = await client.CopyObjectAsync(req, cancellationToken).AnyContext();
                if (!res.HttpStatusCode.IsSuccessful())
                    return false;

                var delReq = new DeleteObjectRequest {
                    BucketName = _bucket,
                    Key = path.Replace('\\', '/')
                };

                var delRes = await client.DeleteObjectAsync(delReq, cancellationToken).AnyContext();
                return delRes.HttpStatusCode.IsSuccessful();
            }
        }

        public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            using (var client = CreateClient()) {
                var req = new CopyObjectRequest {
                    SourceBucket = _bucket,
                    SourceKey = path.Replace('\\', '/'),
                    DestinationBucket = _bucket,
                    DestinationKey = targetPath.Replace('\\', '/')
                };

                var res = await client.CopyObjectAsync(req, cancellationToken).AnyContext();
                return res.HttpStatusCode.IsSuccessful();
            }
        }

        public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            using (var client = CreateClient()) {
                var req = new DeleteObjectRequest {
                    BucketName = _bucket,
                    Key = path.Replace('\\', '/')
                };

                var res = await client.DeleteObjectAsync(req, cancellationToken).AnyContext();
                return res.HttpStatusCode.IsSuccessful();
            }
        }

        public async Task DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = new CancellationToken()) {
            var criteria = GetRequestCriteria(searchPattern);

            using (var client = CreateClient()) {
                var listRequest = new ListObjectsV2Request { BucketName = _bucket, Prefix = criteria.Prefix };
                var deleteRequest = new DeleteObjectsRequest { BucketName = _bucket };

                do {
                    var listResponse = await client.ListObjectsV2Async(listRequest, cancellationToken).AnyContext();
                    if (listResponse.IsTruncated)
                        listRequest.ContinuationToken = listResponse.ContinuationToken;
                    else
                        listRequest = null;

                    var keys = listResponse.S3Objects.MatchesPattern(criteria.Pattern).ToList();
                    foreach (var key in keys.Select(o => new KeyVersion { Key = o.Key }).ToList()) {
                        deleteRequest.Objects.Add(key);

                        if (deleteRequest.Objects.Count == 1000) {
                            var deleteResponse = await client.DeleteObjectsAsync(deleteRequest, cancellationToken).AnyContext();
                            if (!deleteResponse.HttpStatusCode.IsSuccessful())
                                throw new Exception("unable to delete files from storage");

                            deleteRequest.Objects.Clear();
                        }
                    }
                } while (listRequest != null);

                if (deleteRequest.Objects.Count > 0) {
                    var deleteResponse = await client.DeleteObjectsAsync(deleteRequest, cancellationToken).AnyContext();
                    if (!deleteResponse.HttpStatusCode.IsSuccessful())
                        throw new Exception("unable to delete files from storage");
                }
            }
        }

        public async Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (limit.HasValue && limit.Value <= 0)
                return new List<FileSpec>();

            var criteria = GetRequestCriteria(searchPattern);

            var objects = new List<S3Object>();
            using (var client = CreateClient()) {
                var req = new ListObjectsV2Request {
                    BucketName = _bucket,
                    Prefix = criteria.Prefix
                };

                ListObjectsV2Response res;
                do {
                    res = await client.ListObjectsV2Async(req, cancellationToken).AnyContext();
                    objects.AddRange(res.S3Objects.MatchesPattern(criteria.Pattern));
                    req.ContinuationToken = res.NextContinuationToken;
                } while (res.IsTruncated && objects.Count < limit.GetValueOrDefault(int.MaxValue));

                if (limit.HasValue)
                    objects = objects.Take(limit.Value).ToList();

                return objects.Select(blob => blob.ToFileInfo());
            }
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

        public void Dispose() {}
    }
}
