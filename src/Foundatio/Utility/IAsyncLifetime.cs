using System;
using System.Threading.Tasks;

namespace Foundatio.Utility;

/// <summary>
/// Represents a type that requires async initialization before use and async cleanup on disposal.
/// Useful for resources that need to establish connections or perform setup asynchronously.
/// </summary>
public interface IAsyncLifetime : IAsyncDisposable
{
    /// <summary>
    /// Initializes the instance asynchronously. Must be called before using the instance.
    /// </summary>
    Task InitializeAsync();
}
