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
using Foundatio.Logging;

namespace Foundatio.Storage {
    public class S3Storage : IFileStorage {
        private readonly AWSCredentials _credentials;
        private readonly RegionEndpoint _region;
        private readonly string _bucket;
        private readonly ILogger _logger;

        public S3Storage(AWSCredentials credentials, RegionEndpoint region, string bucket = "storage", ILoggerFactory loggerFactory = null) {
            _credentials = credentials;
            _region = region;
            _bucket = bucket;
            _logger = loggerFactory.CreateLogger<S3Storage>();
        }

        private AmazonS3Client CreateClient() {
            return new AmazonS3Client(_credentials, _region);
        }

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            using (var client = CreateClient()) {
                var req = new GetObjectRequest {
                    BucketName = _bucket,
                    Key = path.Replace('\\', '/')
                };

                var res = await client.GetObjectAsync(req, cancellationToken).AnyContext();
                if (!res.HttpStatusCode.IsSuccessful())
                    return null;
                
                return res.ResponseStream;
            }
        }

        public async Task<FileSpec> GetFileInfoAsync(string path) {
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
                        Created = res.LastModified,
                        Modified = res.LastModified,
                        Path = path
                    };
                } catch (AmazonS3Exception) {
                    return null;
                }
            }
        }

        public async Task<bool> ExistsAsync(string path) {
            var result = await GetFileInfoAsync(path).AnyContext();
            return result != null;
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken)) {
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
            using (var client = CreateClient()) {
                var req = new DeleteObjectRequest {
                    BucketName = _bucket,
                    Key = path.Replace('\\', '/')
                };

                var res = await client.DeleteObjectAsync(req, cancellationToken).AnyContext();
                return res.HttpStatusCode.IsSuccessful();
            }
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

            var objects = new List<S3Object>();
            using (var client = CreateClient()) {
                var req = new ListObjectsRequest {
                    BucketName = _bucket,
                    Prefix = prefix
                };

                do {
                    var res = await client.ListObjectsAsync(req, cancellationToken).AnyContext();
                    if (res.IsTruncated)
                        req.Marker = res.NextMarker;
                    else
                        req = null;

                    // TODO: Implement paging
                    objects.AddRange(res.S3Objects.MatchesPattern(patternRegex));
                }
                while (req != null && objects.Count < limit);

                if (limit.HasValue)
                    objects = objects.Take(limit.Value).ToList();

                return objects.Select(blob => blob.ToFileInfo());
            }
        }

        public void Dispose() {}
    }
}
