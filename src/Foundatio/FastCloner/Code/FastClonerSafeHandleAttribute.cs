#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
namespace Foundatio.FastCloner;

/// <summary>
///     Apply this attribute to a struct to indicate it should be treated as a "Safe Handle" or identity-preserving type.
///     When applied, FastCloner will NOT attempt to deep-clone readonly fields on this struct, preventing
///     identity breakage for types that rely on specific internal singleton or handle references.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
internal class FastClonerSafeHandleAttribute : Attribute
{
}


