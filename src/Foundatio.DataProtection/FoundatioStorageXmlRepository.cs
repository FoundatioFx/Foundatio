using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Foundatio.Resilience;
using Foundatio.Storage;
using Foundatio.Utility;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.DataProtection;

/// <summary>
/// An <see cref="IXmlRepository"/> which is backed by Foundatio Storage.
/// </summary>
/// <remarks>
/// Instances of this type are thread-safe.
/// </remarks>
public sealed class FoundatioStorageXmlRepository : IXmlRepository
{
    private readonly IFileStorage _storage;
    private readonly ILogger _logger;
    private readonly IResiliencePolicy _resiliencePolicy;

    /// <summary>
    /// Creates a new instance of the <see cref="FoundatioStorageXmlRepository"/>.
    /// </summary>
    public FoundatioStorageXmlRepository(IFileStorage storage, ILoggerFactory? loggerFactory = null) : this(storage, null, loggerFactory)
    {
    }

    /// <summary>
    /// Creates a new instance of the <see cref="FoundatioStorageXmlRepository"/>.
    /// </summary>
    public FoundatioStorageXmlRepository(IFileStorage storage, IResiliencePolicyProvider? resiliencePolicyProvider, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(storage);

        _storage = new ScopedFileStorage(storage, "DataProtection");
        _logger = loggerFactory?.CreateLogger<FoundatioStorageXmlRepository>() ?? NullLogger<FoundatioStorageXmlRepository>.Instance;

        var policyProvider = resiliencePolicyProvider ?? storage.GetResiliencePolicyProvider() ?? DefaultResiliencePolicyProvider.Instance;
        _resiliencePolicy = policyProvider.GetPolicy<FoundatioStorageXmlRepository>(_logger);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        return GetAllElementsAsync().GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyCollection<XElement>> GetAllElementsAsync()
    {
        _logger.LogTrace("Loading elements...");
        var files = (await _storage.GetFileListAsync("*.xml").AnyContext()).ToList();
        if (files.Count == 0)
        {
            _logger.LogTrace("No elements were found");
            return [];
        }

        _logger.LogTrace("Found {FileCount} elements", files.Count);
        var elements = new List<XElement>(files.Count);
        foreach (var file in files)
        {
            _logger.LogTrace("Loading element: {File}", file.Path);
            await using var stream = await _storage.GetFileStreamAsync(file.Path, StreamMode.Read).AnyContext();
            if (stream is null)
            {
                _logger.LogWarning("Skipping element {File}: file stream was null (file may have been deleted)", file.Path);
                continue;
            }

            elements.Add(XElement.Load(stream));

            _logger.LogTrace("Loaded element: {File}", file.Path);
        }

        return elements.AsReadOnly();
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        StoreElementAsync(element, friendlyName).GetAwaiter().GetResult();
    }

    private Task StoreElementAsync(XElement element, string friendlyName)
    {
        string path = String.Concat(!String.IsNullOrEmpty(friendlyName) ? friendlyName : Guid.NewGuid().ToString("N"), ".xml");
        _logger.LogTrace("Saving element: {File}.", path);

        return _resiliencePolicy.ExecuteAsync(async ct =>
        {
            using var memoryStream = new MemoryStream();
            element.Save(memoryStream, SaveOptions.DisableFormatting);
            memoryStream.Seek(0, SeekOrigin.Begin);

            await _storage.SaveFileAsync(path, memoryStream, ct).AnyContext();
            _logger.LogTrace("Saved element: {File}.", path);
        }).AsTask();
    }
}
