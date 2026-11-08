using System;

namespace Foundatio.Messaging;

/// <summary>The wire name and destinations of a message type.</summary>
internal sealed record MessageRoute(Type Type, string Name, string Queue, string Topic);
