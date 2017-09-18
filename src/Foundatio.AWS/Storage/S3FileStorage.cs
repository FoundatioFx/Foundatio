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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Storage {
    [Obsolete("S3Storage has been renamed to S3FileStorage")]
    public class S3Storage : S3FileStorage {
        public S3Storage(AWSCredentials credentials, RegionEndpoint region, string bucket = "storage", ILoggerFactory loggerFactory = null) : base(credentials, region, bucket, loggerFactory) {}
    }

    public class S3FileStorage : IFileStorage {
        private readonly AWSCredentials _credentials;
        private readonly RegionEndpoint _region;
        private readonly string _bucket;
        private readonly ILogger _logger;

        public S3FileStorage(AWSCredentials credentials, RegionEndpoint region, string bucket = "storage", ILoggerFactory loggerFactory = null) {
            _credentials = credentials;
            _region = region;
            _bucket = bucket;
            _logger = loggerFactory?.CreateLogger<S3FileStorage>() ?? NullLogger<S3FileStorage>.Instance;
        }

        private AmazonS3Client CreateClient() {
            return new AmazonS3Client(_credentials, _region);
        }

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
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
            if (String.IsNullOrWhiteSpace(path))
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
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            var result = await GetFileInfoAsync(path).AnyContext();
            return result != null;
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            using (var client = CreateClient()) {
                var req = new PutObjectRequest {
                    BucketName = _bucket,
                    Key = path.Replace('\\', '/'),
                    InputStream = AmazonS3Util.MakeStreamSeekable(stream)
                };

                var res = await client.PutObjectAsync(req, cancellationToken).AnyContext();
                return res.HttpStatusCode.IsSuccessful();
            }
        }

        public async Task<bool> RenameFileAsync(string oldpath, string newpath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(oldpath))
                throw new ArgumentNullException(nameof(oldpath));
            if (String.IsNullOrWhiteSpace(newpath))
                throw new ArgumentNullException(nameof(newpath));

            using (var client = CreateClient()) {
                var req = new CopyObjectRequest {
                    SourceBucket = _bucket,
                    SourceKey = oldpath.Replace('\\', '/'),
                    DestinationBucket = _bucket,
                    DestinationKey = newpath.Replace('\\', '/')
                };

                var res = await client.CopyObjectAsync(req, cancellationToken).AnyContext();
                if (!res.HttpStatusCode.IsSuccessful())
                    return false;

                var delReq = new DeleteObjectRequest {
                    BucketName = _bucket,
                    Key = oldpath.Replace('\\', '/')
                };

                var delRes = await client.DeleteObjectAsync(delReq, cancellationToken).AnyContext();
                return delRes.HttpStatusCode.IsSuccessful();
            }
        }

        public async Task<bool> CopyFileAsync(string path, string targetpath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrWhiteSpace(targetpath))
                throw new ArgumentNullException(nameof(targetpath));

            using (var client = CreateClient()) {
                var req = new CopyObjectRequest {
                    SourceBucket = _bucket,
                    SourceKey = path.Replace('\\', '/'),
                    DestinationBucket = _bucket,
                    DestinationKey = targetpath.Replace('\\', '/')
                };

                var res = await client.CopyObjectAsync(req, cancellationToken).AnyContext();
                return res.HttpStatusCode.IsSuccessful();
            }
        }

        public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrWhiteSpace(path))
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
                var listRequest = new ListObjectsRequest { BucketName = _bucket, Prefix = criteria.Prefix };
                var deleteRequest = new DeleteObjectsRequest { BucketName = _bucket };

                do {
                    var listResponse = await client.ListObjectsAsync(listRequest, cancellationToken).AnyContext();
                    if (listResponse.IsTruncated)
                        listRequest.Marker = listResponse.NextMarker;
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
                var req = new ListObjectsRequest {
                    BucketName = _bucket,
                    Prefix = criteria.Prefix
                };

                do {
                    var res = await client.ListObjectsAsync(req, cancellationToken).AnyContext();
                    if (res.IsTruncated)
                        req.Marker = res.NextMarker;
                    else
                        req = null;

                    // TODO: Implement paging
                    objects.AddRange(res.S3Objects.MatchesPattern(criteria.Pattern));
                } while (req != null && objects.Count < limit.GetValueOrDefault(int.MaxValue));

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
