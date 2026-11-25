using System.Text.Json.Serialization;

namespace Foundatio.Monitoring;

/// <summary>Serializes <see cref="InstanceSnapshot"/> without reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InstanceSnapshot))]
internal sealed partial class MonitoringJsonContext : JsonSerializerContext;
