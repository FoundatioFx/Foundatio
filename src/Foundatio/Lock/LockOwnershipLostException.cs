using System;

namespace Foundatio.Lock;

/// <summary>The resource lock is no longer owned by this lease holder.</summary>
public sealed class LockOwnershipLostException(string message) : InvalidOperationException(message);
