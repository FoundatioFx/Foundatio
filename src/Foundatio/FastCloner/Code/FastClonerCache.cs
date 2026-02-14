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
/// Specifies how a type should be handled during cloning.
/// </summary>
internal enum CloneBehavior
{
    /// <summary>
    /// Perform deep cloning (default behavior).
    /// </summary>
    Clone,
    
    /// <summary>
    /// Return the same instance without cloning (for immutable/safe types).
    /// </summary>
    Reference,
    
    /// <summary>
    /// Perform shallow cloning (MemberwiseClone).
    /// </summary>
    Shallow,

    /// <summary>
    /// Skip cloning, return default.
    /// </summary>
    Ignore
}

internal static class FastClonerCache
{
    internal static readonly ConcurrentDictionary<Type, CloneBehavior> TypeBehaviors = [];

    internal static bool IsTypeIgnored(Type type)
    {
        return TypeBehaviors.TryGetValue(type, out CloneBehavior behavior) && behavior == CloneBehavior.Ignore;
    }

    internal static volatile bool HasSafeTypeOverrides;

    internal static void RecalculateSafeTypeOverrides()
    {
        foreach (KeyValuePair<Type, CloneBehavior> kvp in TypeBehaviors)
        {
            if (kvp.Value == CloneBehavior.Ignore && FastClonerSafeTypes.DefaultKnownTypes.ContainsKey(kvp.Key))
            {
                HasSafeTypeOverrides = true;
                return;
            }
        }
        HasSafeTypeOverrides = false;
    }
    
    internal static bool IsTypeReference(Type type)
    {
        return TypeBehaviors.TryGetValue(type, out CloneBehavior behavior) && behavior == CloneBehavior.Reference;
    }
    
    internal static CloneBehavior? GetTypeBehavior(Type type)
    {
        return TypeBehaviors.TryGetValue(type, out CloneBehavior behavior) ? behavior : null;
    }
    
    private static readonly ClrCache<object?> classCache = new ClrCache<object?>();
    private static readonly ClrCache<object?> structCache = new ClrCache<object?>();
    private static readonly ClrCache<object> deepClassToCache = new ClrCache<object>();
    private static readonly ClrCache<object> shallowClassToCache = new ClrCache<object>();
    private static readonly ConcurrentLazyCache<object> typeConvertCache = new ConcurrentLazyCache<object>();
    private static readonly GenericClrCache<Tuple<Type, string>, object?> fieldCache = new GenericClrCache<Tuple<Type, string>, object?>();
    private static readonly ClrCache<Dictionary<string, Type>> ignoredEventInfoCache = new ClrCache<Dictionary<string, Type>>();
    private static readonly ClrCache<List<MemberInfo>> allMembersCache = new ClrCache<List<MemberInfo>>();
    private static readonly GenericClrCache<MemberInfo, CloneBehavior?> memberBehaviorCache = new GenericClrCache<MemberInfo, CloneBehavior?>();
    private static readonly ClrCache<bool> typeContainsIgnoredMembersCache = new ClrCache<bool>();
    private static readonly ClrCache<object> specialTypesCache = new ClrCache<object>();
    private static readonly ClrCache<bool> isTypeSafeHandleCache = new ClrCache<bool>();
    private static readonly ClrCache<bool> anonymousTypeStatusCache = new ClrCache<bool>();
    private static readonly ClrCache<bool> stableHashSemanticsCache = new ClrCache<bool>();

    public static object? GetOrAddField(Type type, string name, Func<Type, object?> valueFactory) => fieldCache.GetOrAdd(new Tuple<Type, string>(type, name), k => valueFactory(k.Item1));
    public static object? GetOrAddClass(Type type, Func<Type, object?> valueFactory) => classCache.GetOrAdd(type, valueFactory);
    public static object? GetOrAddStructAsObject(Type type, Func<Type, object?> valueFactory) => structCache.GetOrAdd(type, valueFactory);
    public static object GetOrAddDeepClassTo(Type type, Func<Type, object> valueFactory) => deepClassToCache.GetOrAdd(type, valueFactory);
    public static object GetOrAddShallowClassTo(Type type, Func<Type, object> valueFactory) => shallowClassToCache.GetOrAdd(type, valueFactory);
    public static T GetOrAddConvertor<T>(Type from, Type to, Func<Type, Type, T> valueFactory) => (T)typeConvertCache.GetOrAdd(from, to, (f, t) => valueFactory(f, t));
    public static Dictionary<string, Type> GetOrAddIgnoredEventInfo(Type type, Func<Type, Dictionary<string, Type>> valueFactory) => ignoredEventInfoCache.GetOrAdd(type, valueFactory);
    public static List<MemberInfo> GetOrAddAllMembers(Type type, Func<Type, List<MemberInfo>> valueFactory) => allMembersCache.GetOrAdd(type, valueFactory);
    public static CloneBehavior? GetOrAddMemberBehavior(MemberInfo memberInfo, Func<MemberInfo, CloneBehavior?> valueFactory) => memberBehaviorCache.GetOrAdd(memberInfo, valueFactory);
    public static bool GetOrAddTypeContainsIgnoredMembers(Type type, Func<Type, bool> valueFactory)
    {
        return type.IsValueType && typeContainsIgnoredMembersCache.GetOrAdd(type, valueFactory);
    }
    public static object GetOrAddSpecialType(Type type, Func<Type, object> valueFactory) => specialTypesCache.GetOrAdd(type, valueFactory);
    public static bool GetOrAddIsTypeSafeHandle(Type type, Func<Type, bool> valueFactory) => isTypeSafeHandleCache.GetOrAdd(type, valueFactory);
    public static bool GetOrAddAnonymousTypeStatus(Type type, Func<Type, bool> valueFactory) => anonymousTypeStatusCache.GetOrAdd(type, valueFactory);
    public static bool GetOrAddStableHashSemantics(Type type, Func<Type, bool> valueFactory) => stableHashSemanticsCache.GetOrAdd(type, valueFactory);
    
    /// <summary>
    /// Clears the FastCloner cached reflection metadata.
    /// </summary>
    public static void ClearCache()
    {
        classCache.Clear();
        structCache.Clear();
        deepClassToCache.Clear();
        shallowClassToCache.Clear();
        typeConvertCache.Clear();
        fieldCache.Clear();
        ignoredEventInfoCache.Clear();
        allMembersCache.Clear();
        memberBehaviorCache.Clear();
        typeContainsIgnoredMembersCache.Clear();
        specialTypesCache.Clear();
        isTypeSafeHandleCache.Clear();
        anonymousTypeStatusCache.Clear();
        stableHashSemanticsCache.Clear();
    }
    
    internal sealed class ClrCache<TValue>
    {
        private readonly ConcurrentDictionary<IntPtr, TValue> cache = new ConcurrentDictionary<IntPtr, TValue>();
        
        public TValue GetOrAdd(Type type, Func<Type, TValue> valueFactory)
        {
            IntPtr handle = type.TypeHandle.Value;
            return cache.TryGetValue(handle, out TValue? cached) ? cached : cache.GetOrAdd(handle, _ => valueFactory(type));
        }

        public void Clear() => cache.Clear();
    }
    
    private sealed class GenericClrCache<TKey, TValue> where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, TValue> cache = new ConcurrentDictionary<TKey, TValue>();
        
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            return cache.TryGetValue(key, out TValue? value) ? value : cache.GetOrAdd(key, valueFactory);
        }

        public void Clear() => cache.Clear();
    }
    
    private sealed class ConcurrentLazyCache<TValue>
    {
#if true // MODERN
        private readonly ConcurrentDictionary<(IntPtr, IntPtr), TValue> cache = new ConcurrentDictionary<(IntPtr, IntPtr), TValue>();

        public TValue GetOrAdd(Type from, Type to, Func<Type, Type, TValue> valueFactory)
        {
            (IntPtr, IntPtr) key = (from.TypeHandle.Value, to.TypeHandle.Value);
            return cache.TryGetValue(key, out TValue? cached) ? cached : cache.GetOrAdd(key, _ => valueFactory(from, to));
        }

        public void Clear() => cache.Clear();
#else
        private readonly ConcurrentDictionary<Tuple<IntPtr, IntPtr>, TValue> cache = new ConcurrentDictionary<Tuple<IntPtr, IntPtr>, TValue>();
        
        public TValue GetOrAdd(Type from, Type to, Func<Type, Type, TValue> valueFactory)
        {
            Tuple<IntPtr, IntPtr> key = Tuple.Create(from.TypeHandle.Value, to.TypeHandle.Value);
            return cache.TryGetValue(key, out TValue? cached) ? cached : cache.GetOrAdd(key, _ => valueFactory(from, to));
        }

        public void Clear() => cache.Clear();
#endif
    }
}

