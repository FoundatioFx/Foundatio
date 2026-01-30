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
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Foundatio.FastCloner.Code;

/// <summary>
/// Safe types are types, which can be copied without real cloning. e.g. simple structs or strings (it is immutable)
/// </summary>
internal static class FastClonerSafeTypes
{
    internal static readonly Dictionary<Type, bool> DefaultKnownTypes = new Dictionary<Type, bool>(64)
    {
        // Primitives
        [typeof(byte)] = true,
        [typeof(short)] = true,
        [typeof(ushort)] = true,
        [typeof(int)] = true,
        [typeof(uint)] = true,
        [typeof(long)] = true,
        [typeof(ulong)] = true,
        [typeof(float)] = true,
        [typeof(double)] = true,
        [typeof(decimal)] = true,
        [typeof(string)] = true,
        [typeof(char)] = true,
        [typeof(bool)] = true,
        [typeof(sbyte)] = true,
        [typeof(nint)] = true,
        [typeof(nuint)] = true,
        [typeof(Guid)] = true,
#if true // MODERN
        [typeof(Rune)] = true,
#endif

        // Time-related types
        [typeof(TimeSpan)] = true,
        [typeof(TimeZoneInfo)] = true,
        [typeof(DateTime)] = true,
        [typeof(DateTimeOffset)] = true,
#if true // MODERN
        [typeof(DateOnly)] = true,
        [typeof(TimeOnly)] = true,
#endif

        // Numeric types
 #if true // MODERN
        [typeof(Half)] = true,
        [typeof(Int128)] = true,
        [typeof(UInt128)] = true,
        [typeof(Complex)] = true,
#endif
        // Others
        [typeof(DBNull)] = true,
        [StringComparer.Ordinal.GetType()] = true,
        [StringComparer.OrdinalIgnoreCase.GetType()] = true,
        [StringComparer.InvariantCulture.GetType()] = true,
        [StringComparer.InvariantCultureIgnoreCase.GetType()] = true,
        [typeof(WeakReference)] = true,
        [typeof(WeakReference<>)] = true,
        [typeof(CancellationTokenSource)] = true,
#if true // MODERN
        [typeof(Range)] = true,
        [typeof(Index)] = true
#endif
    };

    private static readonly ConcurrentDictionary<Type, bool> knownTypes = [];

    static FastClonerSafeTypes()
    {
        InitializeKnownTypes();
    }

    private static void InitializeKnownTypes()
    {
        foreach (KeyValuePair<Type, bool> x in DefaultKnownTypes)
        {
            knownTypes.TryAdd(x.Key, x.Value);
        }
        
        List<Type?> safeTypes =
        [
            Type.GetType("System.RuntimeType"),
            Type.GetType("System.RuntimeTypeHandle")
        ];

        foreach (Type x in safeTypes.OfType<Type>())
        {
            knownTypes.TryAdd(x, true);
        }
    }
    
    private static bool IsSpecialEqualityComparer(string fullName) => fullName switch
    {
        _ when fullName.StartsWith("System.Collections.Generic.GenericEqualityComparer`") => true,
        _ when fullName.StartsWith("System.Collections.Generic.ObjectEqualityComparer`") => true,
        _ when fullName.StartsWith("System.Collections.Generic.EnumEqualityComparer`") => true,
        _ when fullName.StartsWith("System.Collections.Generic.NullableEqualityComparer`") => true,
        "System.Collections.Generic.ByteEqualityComparer" => true,
        "System.Collections.Generic.StringEqualityComparer" => true,
        _ => false
    };
    
    private static class TypePrefixes
    {
        public const string SystemReflection = "System.Reflection.";
        public const string SystemRuntimeType = "System.RuntimeType";
        public const string MicrosoftExtensions = "Microsoft.Extensions.DependencyInjection.";
    }

    private static readonly Assembly propertyInfoAssembly = typeof(PropertyInfo).Assembly;
    
    private static bool IsReflectionType(Type type)
    {
        if (type == typeof(AssemblyName))
        {
            return false;
        }
    
        return type.FullName?.StartsWith(TypePrefixes.SystemReflection) is true && Equals(type.GetTypeInfo().Assembly, typeof(PropertyInfo).GetTypeInfo().Assembly);
    }

    private static IEnumerable<FieldInfo> GetAllTypeFields(Type type)
    {
        Type? currentType = type;
    
        while (currentType is not null)
        {
            foreach (FieldInfo field in currentType.GetAllFields())
            {
                yield return field;
            }
            
            currentType = currentType.BaseType();
        }
    }
    
    private static bool IsAnonymousType(Type type)
    {
        return FastClonerCache.GetOrAddAnonymousTypeStatus(type, t => 
            t.IsClass && t is { IsSealed: true, IsNotPublic: true }
                      && t.IsDefined(typeof(CompilerGeneratedAttribute), false)
                      && (t.Name.StartsWith("<>") || t.Name.StartsWith("VB$"))
                      && t.Name.Contains("AnonymousType"));
    }

    private static readonly HashSet<string> safeTypeExact = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Threading.Tasks.Task",
        "Microsoft.EntityFrameworkCore.Internal.ConcurrencyDetector"
    };

    private static readonly AhoCorasick safeTypePrefixes = new AhoCorasick([
        TypePrefixes.SystemRuntimeType,
        TypePrefixes.MicrosoftExtensions,
        "System.Threading.Tasks.Task`"
    ]);

    private static bool IsSafeSystemType(Type type)
    {
        if (type.IsEnum() || type.IsPointer)
            return true;

        if (type.IsCOMObject)
            return true;

        string? fullName = type.FullName;

        if (fullName is null)
            return true;

        if (safeTypeExact.Contains(fullName))
            return true;

        if (safeTypePrefixes.ContainsAnyPattern(fullName))
            return true;

        if (IsReflectionType(type))
            return true;

        if (type.IsSubclassOf(typeof(System.Runtime.ConstrainedExecution.CriticalFinalizerObject)))
            return true;

        return false;
    }
    
    private static bool CanReturnSameType(Type type, HashSet<Type>? processingTypes = null)
    {
        if (FastClonerCache.IsTypeReference(type))
        {
            return true;
        }
        
        if (knownTypes.TryGetValue(type, out bool isSafe))
        {
            return isSafe;
        }
        
        if (type.IsGenericType)
        {
            Type? genericDef = type.GetGenericTypeDefinition();
            if (knownTypes.TryGetValue(genericDef, out bool isGenericSafe))
            {
                knownTypes.TryAdd(type, isGenericSafe);
                return isGenericSafe;
            }
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            knownTypes.TryAdd(type, false);
            return false;
        }
        
        string? fullName = type.FullName;

        if (fullName is null || IsSafeSystemType(type) || fullName.Contains("EqualityComparer") && IsSpecialEqualityComparer(fullName))
        {
            knownTypes.TryAdd(type, true);
            return true;
        }

        if (!IsAnonymousType(type) && !type.IsValueType())
        {
            knownTypes.TryAdd(type, false);
            return false;
        }

        processingTypes ??= [];

        if (!processingTypes.Add(type))
        {
            return true;
        }

        foreach (FieldInfo fieldInfo in GetAllTypeFields(type))
        {
            Type fieldType = fieldInfo.FieldType;
            
            if (processingTypes.Contains(fieldType))
            {
                continue;
            }

            if (CanReturnSameType(fieldType, processingTypes))
            {
                continue;
            }
            
            knownTypes.TryAdd(type, false);
            return false;
        }

        knownTypes.TryAdd(type, true);
        return true;
    }

    public static bool CanReturnSameObject(Type type) => CanReturnSameType(type);
    
    internal static void ClearKnownTypesCache()
    {
        knownTypes.Clear();
        InitializeKnownTypes();
    }
    
    /// <summary>
    /// Determines whether GetHashCode() result won't change after deep cloning (best effort).
    /// </summary>
    internal static bool HasStableHashSemantics(Type type)
    {
        return FastClonerCache.GetOrAddStableHashSemantics(type, CalculateHasStableHashSemantics);
    }
    
    private static bool CalculateHasStableHashSemantics(Type type)
    {
        // Primitives are always stable - their hash is based on their value
        if (type.IsPrimitive)
            return true;
        
        // String is immutable and has value-based hash
        if (type == typeof(string))
            return true;
        
        // Enums are always stable - hash is based on underlying value
        if (type.IsEnum)
            return true;
        
        // Value types: even if they don't override GetHashCode, their fields are copied
        // so the hash remains consistent after cloning
        if (type.IsValueType)
            return true;
        
        // Known safe types from our dictionary are stable
        if (DefaultKnownTypes.ContainsKey(type))
            return true;
        
        // Check if the type overrides GetHashCode (not using object.GetHashCode)
        // If a type has overridden GetHashCode, it's using value-based hashing
        MethodInfo? getHashCodeMethod = type.GetMethod(
            "GetHashCode",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            Type.EmptyTypes,
            null);
        
        if (getHashCodeMethod is not null && getHashCodeMethod.DeclaringType != typeof(object))
        {
            // Type has custom GetHashCode implementation
            // This indicates value-based equality semantics
            return true;
        }
        
        // Reference types using default GetHashCode use identity-based hash
        // These are NOT stable after cloning
        return false;
    }
}

