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
/// Marks a member or type for shallow cloning instead of deep cloning.
/// When applied to a type, all usages perform MemberwiseClone without recursing into members.
/// When applied to a member, that reference is copied directly without deep cloning its contents.
/// This is useful for parent references, shared state, or when deep cloning would cause issues.
/// </summary>
/// <example>
/// <code>
/// // Type-level: shallow clone the entire Config type
/// [FastClonerShallow]
/// internal class Config { }
/// 
/// // Member-level: shallow clone only this member
/// internal class Node
/// {
///     [FastClonerShallow]
///     public ParentObject Parent { get; set; }
/// }
/// </code>
/// </example>
/// <remarks>
/// This attribute is a shorthand for <c>[FastClonerBehavior(CloneBehavior.Shallow)]</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
internal class FastClonerShallowAttribute : FastClonerBehaviorAttribute
{
    /// <summary>
    /// Initializes a new instance of <see cref="FastClonerShallowAttribute"/>.
    /// </summary>
    public FastClonerShallowAttribute() : base(CloneBehavior.Shallow)
    {
    }
}

