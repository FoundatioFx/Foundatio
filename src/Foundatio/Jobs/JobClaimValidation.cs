using System;
using System.Linq;

namespace Foundatio.Jobs;

internal static class JobClaimValidation
{
    public static void Validate(JobClaimRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NodeId);
        ArgumentNullException.ThrowIfNull(request.JobTypes);
        if (request.JobTypes.Count == 0 || request.JobTypes.Any(String.IsNullOrWhiteSpace))
            throw new ArgumentException("Register the job types this worker can execute.", nameof(request));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Lease, TimeSpan.Zero);
    }
}
