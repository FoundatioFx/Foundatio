using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
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

    /// <summary>
    /// Creates a new instance of the <see cref="FoundatioStorageXmlRepository"/>.
    /// </summary>
    public FoundatioStorageXmlRepository(IFileStorage storage, ILoggerFactory loggerFactory = null)
    {
        if (storage == null)
            throw new ArgumentNullException(nameof(storage));

        _storage = new ScopedFileStorage(storage, "DataProtection");
        _logger = loggerFactory?.CreateLogger<FoundatioStorageXmlRepository>() ?? NullLogger<FoundatioStorageXmlRepository>.Instance;
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
            return new XElement[0];
        }

        _logger.LogTrace("Found {FileCount} elements.", files.Count);
        var elements = new List<XElement>(files.Count);
        foreach (var file in files)
        {
            _logger.LogTrace("Loading element: {File}", file.Path);
            using (var stream = await _storage.GetFileStreamAsync(file.Path).AnyContext())
            {
                elements.Add(XElement.Load(stream));
            }

            _logger.LogTrace("Loaded element: {File}", file.Path);
        }

        return elements.AsReadOnly();
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        if (element == null)
            throw new ArgumentNullException(nameof(element));

        StoreElementAsync(element, friendlyName).GetAwaiter().GetResult();
    }

    private Task StoreElementAsync(XElement element, string friendlyName)
    {
        string path = String.Concat(!String.IsNullOrEmpty(friendlyName) ? friendlyName : Guid.NewGuid().ToString("N"), ".xml");
        _logger.LogTrace("Saving element: {File}.", path);

        return Run.WithRetriesAsync(async () =>
        {
            using var memoryStream = new MemoryStream();
            element.Save(memoryStream, SaveOptions.DisableFormatting);
            memoryStream.Seek(0, SeekOrigin.Begin);

            await _storage.SaveFileAsync(path, memoryStream).AnyContext();
            _logger.LogTrace("Saved element: {File}.", path);
        });
    }
}
