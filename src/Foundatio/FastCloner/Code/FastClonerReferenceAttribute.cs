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
/// Marks a member or type to preserve the original reference during cloning.
/// When applied to a type, all usages of that type will share the same reference.
/// When applied to a member, that specific member will share the same reference.
/// This is useful for shared services, singletons, or immutable objects that should not be duplicated.
/// </summary>
/// <example>
/// <code>
/// // Type-level: all usages of ILogger preserve reference
/// [FastClonerReference]
/// internal class LoggerService : ILogger { }
/// 
/// // Member-level: only this property preserves reference
/// internal class MyClass
/// {
///     [FastClonerReference]
///     public ILogger Logger { get; set; }
/// }
/// </code>
/// </example>
/// <remarks>
/// This attribute is a shorthand for <c>[FastClonerBehavior(CloneBehavior.Reference)]</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
internal class FastClonerReferenceAttribute : FastClonerBehaviorAttribute
{
    /// <summary>
    /// Initializes a new instance of <see cref="FastClonerReferenceAttribute"/>.
    /// </summary>
    public FastClonerReferenceAttribute() : base(CloneBehavior.Reference)
    {
    }
}

