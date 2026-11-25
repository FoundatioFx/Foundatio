using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Jobs.Store;

/// <summary>The result of <see cref="IJobStore.RenewAsync"/>.</summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public enum JobLeaseStatus
{
    /// <summary>The lease was extended.</summary>
    Renewed,

    /// <summary>Renewed, and cancellation has been requested.</summary>
    CancellationRequested,

    /// <summary>The claim token no longer holds the job.</summary>
    Lost
}
