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
namespace Foundatio.FastCloner.Code;

/// <summary>
/// Specifies the cloning behavior for a member (field, property, event) or an entire type (class, struct, record).
/// When applied to a type, all members of that type will use this behavior unless overridden at the member level.
/// This is the base attribute for configuring clone behavior.
/// </summary>
/// <example>
/// <code>
/// // Type-level: all usages of SharedService will preserve reference
/// [FastClonerBehavior(CloneBehavior.Reference)]
/// internal class SharedService { }
/// 
/// // Member-level: override behavior for specific members
/// internal class MyClass
/// {
///     [FastClonerBehavior(CloneBehavior.Ignore)]
///     public CancellationToken Token { get; set; }  // Set to default
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
internal class FastClonerBehaviorAttribute(CloneBehavior behavior) : Attribute
{
    /// <summary>
    /// Gets the cloning behavior.
    /// </summary>
    public CloneBehavior Behavior { get; } = behavior;
}

