namespace Foundatio.Caching;

/// <summary>
/// Marker interface for in-memory cache implementations.
/// Used to identify caches that store data in process memory rather than external stores.
/// </summary>
public interface IMemoryCacheClient : ICacheClient
{
}
