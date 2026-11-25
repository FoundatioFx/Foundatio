namespace Foundatio;

/// <summary>
/// The diagnostic reported for the provider-facing API (the transport and job store contracts and their conformance
/// suites), which may still change in a minor release. Apps that use the provider helpers never see it.
/// </summary>
internal static class ProviderApi
{
    public const string DiagnosticId = "FOUNDATIO0001";
    public const string UrlFormat = "https://foundatio.dev/guide/implementing-providers#experimental-api";
}
