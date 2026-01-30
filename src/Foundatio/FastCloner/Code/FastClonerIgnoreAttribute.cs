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
/// Marks a member or type as ignored, effectively assigning a default value when cloning.
/// When applied to a type, all usages of that type will be set to default.
/// When applied to a member, that specific member will be set to default.
/// </summary>
/// <example>
/// <code>
/// // Type-level: all usages of CancellationToken get default value
/// [FastClonerIgnore]
/// internal struct MyCancellationWrapper { }
/// 
/// // Member-level
/// internal class MyClass
/// {
///     [FastClonerIgnore]
///     public CancellationToken Token { get; set; }
/// }
/// </code>
/// </example>
/// <remarks>
/// This attribute is a shorthand for <c>[FastClonerBehavior(CloneBehavior.Ignore)]</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
internal class FastClonerIgnoreAttribute : FastClonerBehaviorAttribute
{
    /// <summary>
    /// Initializes a new instance of <see cref="FastClonerIgnoreAttribute"/>.
    /// </summary>
    /// <param name="ignored">Whether the member/type should be ignored during cloning. Default is true.</param>
    public FastClonerIgnoreAttribute(bool ignored = true) 
        : base(ignored ? CloneBehavior.Ignore : CloneBehavior.Clone)
    {
    }
    
    /// <summary>
    /// Gets whether the member/type should be ignored during cloning.
    /// </summary>
    public bool Ignored => Behavior == CloneBehavior.Ignore;
}
