namespace Foundatio.Serializer;

/// <summary>
/// Indicates that a type exposes a serializer for data conversion.
/// Used by infrastructure to access serialization capabilities.
/// </summary>
public interface IHaveSerializer
{
    /// <summary>
    /// Gets the serializer used for data conversion.
    /// </summary>
    ISerializer Serializer { get; }
}
