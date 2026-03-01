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
using Foundatio.FastCloner.Code;

namespace Foundatio.FastCloner;

/// <summary>
/// Extensions for object cloning
/// </summary>
internal static class FastCloner
{
    /// <summary>
    /// Cloning objects with nest level above this threshold uses iterative approach instead of recursion.
    /// </summary>
    public static int MaxRecursionDepth { get; set; } = 1_000;
    
    /// <summary>
    /// Performs deep (full) copy of object and related graph
    /// </summary>
    public static T? DeepClone<T>(T? obj) => FastClonerGenerator.CloneObject(obj);

    /// <summary>
    /// Performs deep (full) copy of object and related graph to existing object
    /// </summary>
    /// <returns>existing filled object</returns>
    /// <remarks>Method is valid only for classes, classes should be descendants in reality, not in declaration</remarks>
    public static TTo? DeepCloneTo<TFrom, TTo>(TFrom? objFrom, TTo? objTo) where TTo : class, TFrom => (TTo?)FastClonerGenerator.CloneObjectTo(objFrom, objTo, true);

    /// <summary>
    /// Performs shallow copy of object to existing object
    /// </summary>
    /// <returns>existing filled object</returns>
    /// <remarks>Method is valid only for classes, classes should be descendants in reality, not in declaration</remarks>
    public static TTo? ShallowCloneTo<TFrom, TTo>(TFrom? objFrom, TTo? objTo) where TTo : class, TFrom => (TTo?)FastClonerGenerator.CloneObjectTo(objFrom, objTo, false);

    /// <summary>
    /// Performs shallow (only new object returned, without cloning of dependencies) copy of object
    /// </summary>
    public static T? ShallowClone<T>(T? obj) => ShallowClonerGenerator.CloneObject(obj);

    /// <summary>
    /// Clears all cached information about classes, structs, types, and other CLR objects.
    /// </summary>
    public static void ClearCache() => FastClonerCache.ClearCache();
    
    /// <summary>
    /// Sets the cloning behavior for a type.
    /// <list type="bullet">
    /// <item><description><see cref="CloneBehavior.Clone"/> - Default behavior, performs deep cloning (removes any custom behavior).</description></item>
    /// <item><description><see cref="CloneBehavior.Reference"/> - Returns the same instance without cloning (for immutable/safe types).</description></item>
    /// <item><description><see cref="CloneBehavior.Ignore"/> - Returns null/default and skips cloning entirely.</description></item>
    /// </list>
    /// </summary>
    /// <param name="type">The type to configure.</param>
    /// <param name="behavior">The cloning behavior to apply.</param>
    /// <remarks>
    /// Setting <see cref="CloneBehavior.Clone"/> removes any custom behavior (equivalent to <see cref="ClearTypeBehavior"/>).
    /// Note that changing behavior clears the cache, which may impact performance until the cache is repopulated.
    /// </remarks>
    public static void SetTypeBehavior(Type type, CloneBehavior behavior)
    {
        if (behavior == CloneBehavior.Clone)
        {
            // Clone is the default - remove any custom behavior
            if (FastClonerCache.TypeBehaviors.TryRemove(type, out _))
            {
                FastClonerSafeTypes.ClearKnownTypesCache();
                FastClonerCache.ClearCache();
                FastClonerCache.RecalculateSafeTypeOverrides();
            }
        }
        else
        {
            FastClonerCache.TypeBehaviors[type] = behavior;
            FastClonerSafeTypes.ClearKnownTypesCache();
            FastClonerCache.ClearCache();
            FastClonerCache.RecalculateSafeTypeOverrides();
        }
    }

    /// <summary>
    /// Sets the cloning behavior for a type.
    /// </summary>
    /// <typeparam name="T">The type to configure.</typeparam>
    /// <param name="behavior">The cloning behavior to apply.</param>
    public static void SetTypeBehavior<T>(CloneBehavior behavior) => SetTypeBehavior(typeof(T), behavior);

    /// <summary>
    /// Gets the configured cloning behavior for a type, or null if using default behavior.
    /// </summary>
    /// <param name="type">The type to query.</param>
    /// <returns>The configured behavior, or null if no custom behavior is set.</returns>
    public static CloneBehavior? GetTypeBehavior(Type type) => FastClonerCache.GetTypeBehavior(type);

    /// <summary>
    /// Gets the configured cloning behavior for a type, or null if using default behavior.
    /// </summary>
    /// <typeparam name="T">The type to query.</typeparam>
    /// <returns>The configured behavior, or null if no custom behavior is set.</returns>
    public static CloneBehavior? GetTypeBehavior<T>() => GetTypeBehavior(typeof(T));

    /// <summary>
    /// Returns all types with custom cloning behaviors configured.
    /// </summary>
    public static Dictionary<Type, CloneBehavior> GetTypeBehaviors()
    {
        return new Dictionary<Type, CloneBehavior>(FastClonerCache.TypeBehaviors);
    }

    /// <summary>
    /// Clears any custom cloning behavior for a type, returning it to default deep clone behavior.
    /// </summary>
    /// <param name="type">The type to clear.</param>
    /// <returns>True if a custom behavior was removed, false if none was set.</returns>
    /// <remarks>
    /// Note that this clears the cache, which may have negative impact on cloning performance until the cache is repopulated.
    /// </remarks>
    public static bool ClearTypeBehavior(Type type)
    {
        bool removed = FastClonerCache.TypeBehaviors.TryRemove(type, out _);
        if (removed)
        {
            FastClonerSafeTypes.ClearKnownTypesCache();
            FastClonerCache.ClearCache();
            FastClonerCache.RecalculateSafeTypeOverrides();
        }
        return removed;
    }

    /// <summary>
    /// Clears any custom cloning behavior for a type, returning it to default deep clone behavior.
    /// </summary>
    /// <typeparam name="T">The type to clear.</typeparam>
    /// <returns>True if a custom behavior was removed, false if none was set.</returns>
    public static bool ClearTypeBehavior<T>() => ClearTypeBehavior(typeof(T));

    /// <summary>
    /// Clears all custom type behaviors, returning all types to default deep clone behavior.
    /// </summary>
    /// <remarks>
    /// Note that this clears the cache, which may have negative impact on cloning performance until the cache is repopulated.
    /// </remarks>
    public static void ClearAllTypeBehaviors()
    {
        FastClonerCache.TypeBehaviors.Clear();
        FastClonerSafeTypes.ClearKnownTypesCache();
        FastClonerCache.ClearCache();
        FastClonerCache.HasSafeTypeOverrides = false; 
    }
}
