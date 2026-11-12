using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Jobs.Store;

[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public enum JobLeaseStatus
{
    Renewed,

    /// <summary>Renewed, and cancellation has been requested.</summary>
    CancellationRequested,

    /// <summary>The claim token no longer holds the job.</summary>
    Lost
}
