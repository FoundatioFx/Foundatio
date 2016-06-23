using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Amazon.S3.Model;
using Foundatio.Storage;

namespace Foundatio.AWS.Extensions {
    internal static class Extensions {
        internal static bool IsSuccessful(this HttpStatusCode code) {
            return (int)code < 400;
        }

        internal static FileSpec ToFileInfo(this S3Object blob) {
            if (blob == null)
                return null;

            return new FileSpec {
                Path = blob.Key,
                Size = blob.Size,
                Modified = blob.LastModified,
                Created = blob.LastModified // TODO: Need to fix this
            };
        }

        internal static IEnumerable<S3Object> MatchesPattern(this IEnumerable<S3Object> blobs, Regex patternRegex) {
            return blobs.Where(blob => patternRegex == null || patternRegex.IsMatch(blob.ToFileInfo().Path));
        }
    }
}
