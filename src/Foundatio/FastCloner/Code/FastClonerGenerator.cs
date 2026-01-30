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

internal static class FastClonerGenerator
{
    public static T? CloneObject<T>(T? obj)
    {
        if (obj is null)
        {
            return default;
        }
        
        Type concreteTypeOfObj = obj.GetType();
        Type typeOfT = typeof(T);
        
        // Check for custom type behaviors first
        CloneBehavior? behavior = FastClonerCache.GetTypeBehavior(concreteTypeOfObj);
        
        switch (behavior)
        {
            case CloneBehavior.Ignore:
                return default;
            case CloneBehavior.Reference:
                return obj;
            case CloneBehavior.Shallow:
                return ShallowClonerGenerator.CloneObject(obj);
        }

        if (FastClonerSafeTypes.DefaultKnownTypes.TryGetValue(concreteTypeOfObj, out _))
        {
            return obj;
        }
        
        switch (obj)
        {
            case ValueType:
            {
                Type type = obj.GetType();
                
                if (typeOfT == type)
                {
                    bool hasIgnoredMembers = FastClonerCache.GetOrAddTypeContainsIgnoredMembers(type, FastClonerExprGenerator.CalculateTypeContainsIgnoredMembers);
                    
                    if (hasIgnoredMembers || !FastClonerSafeTypes.CanReturnSameObject(type))
                    {
                        FastCloneState structState = FastCloneState.Rent();
                        try
                        {
                            return CloneStructInternal(obj, structState);
                        }
                        finally
                        {
                            FastCloneState.Return(structState);
                        }
                    }
                    
                    return obj;
                }

                break;
            }
            case Delegate del:
            {
                Type? targetType = del.Target?.GetType();
            
                if (targetType?.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
                {
                    return (T?)CloneClassRoot(obj);
                }
            
                return obj;
            }
        }

        return (T?)CloneClassRoot(obj);
    }

    private static object? CloneClassRoot(object? obj)
    {
        if (obj == null)
            return null;

        Type rootType = obj.GetType();
        Func<object, FastCloneState, object>? cloner = (Func<object, FastCloneState, object>?)FastClonerCache.GetOrAddClass(rootType, t => GenerateCloner(t, true));

        // null -> should return same type
        if (cloner is null)
        {
            return obj;
        }
        
        FastCloneState state = FastCloneState.Rent();
        state.UseWorkList = TypeHasDirectSelfReference(rootType);
        
        object result;
        
        try
        {
            if (!state.UseWorkList)
            {
                int current = state.IncrementDepth();
                
                if (current >= FastCloner.MaxRecursionDepth)
                {
                    state.DecrementDepth();
                    state.UseWorkList = true;
                    result = cloner(obj, state);
                }
                else
                {
                    result = cloner(obj, state);
                    state.DecrementDepth();
                    
                    // if UseWorkList was set during recursive cloning, process the worklist
                    if (!state.UseWorkList)
                    {
                        return result;
                    }
                }
            }
            else
            {
                result = cloner(obj, state);
            }
            
            while (state.TryPop(out object from, out object to, out Type type))
            {
                // boxed value types - MemberwiseClone already created a value copy.
                if (type.IsValueType())
                {
                    continue;
                }
                
                Func<object, object, FastCloneState, object> clonerTo = (Func<object, object, FastCloneState, object>)FastClonerCache.GetOrAddDeepClassTo(type, t => ClonerToExprGenerator.GenerateClonerInternal(t, true));
                clonerTo(from, to, state);
            }

            return result;
        }
        finally
        {
            FastCloneState.Return(state);
        }
    }

    private static bool TypeHasDirectSelfReference(Type type)
    {
        Type? tp = type;
        while (tp != null && tp != typeof(ContextBoundObject))
        {
            foreach (FieldInfo fi in tp.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                Type ft = fi.FieldType;
                if (ft == type)
                {
                    return true;
                }
                if (ft.IsArray && ft.GetElementType() == type)
                {
                    return true;
                }
            }
            tp = tp.BaseType;
        }
        return false;
    }
    
    internal static object? CloneClassInternal(object? obj, FastCloneState state)
    {
        return obj is null ? null : CloneClassInternalTyped(obj, obj.GetType(), state);
    }

    internal static object? CloneClassInternalTyped(object obj, Type objType, FastCloneState state)
    {
        if (!FastClonerCache.HasSafeTypeOverrides)
        {
             if (FastClonerSafeTypes.DefaultKnownTypes.ContainsKey(objType))
                return obj;
             
             if (FastClonerCache.IsTypeIgnored(objType))
                return null;
        }
        else
        {
             if (FastClonerCache.IsTypeIgnored(objType))
                return null;

             if (FastClonerSafeTypes.DefaultKnownTypes.ContainsKey(objType))
                return obj;
        }

        Func<object, FastCloneState, object>? cloner = (Func<object, FastCloneState, object>?)FastClonerCache.GetOrAddClass(objType, t => GenerateCloner(t, true));

        // safe object
        if (cloner is null)
        {
            return obj;
        }
        
        if (state.UseWorkList)
        {
            object? knownA = state.GetKnownRef(obj);
            if (knownA is not null)
            {
                return knownA;
            }
            
            // value types: avoid the worklist because ClonerToExprGenerator.GenerateClonerInternal doesn't support value types
            if (objType.IsValueType())
            {
                object cloned = cloner(obj, state);
                state.AddKnownRef(obj, cloned);
                return cloned;
            }
            
            return CloneClassShallowAndTrack(obj, state);
        }

        try
        {
            int current = state.IncrementDepth();
            
            if (current >= FastCloner.MaxRecursionDepth)
            {
                state.DecrementDepth();
                state.UseWorkList = true;
                object? knownB = state.GetKnownRef(obj);
                if (knownB is not null)
                {
                    return knownB;
                }
                
                // value types: avoid the worklist because ClonerToExprGenerator.GenerateClonerInternal doesn't support value types
                if (objType.IsValueType())
                {
                    object cloned = cloner(obj, state);
                    state.AddKnownRef(obj, cloned);
                    return cloned;
                }
                
                return CloneClassShallowAndTrack(obj, state);
            }

            object? knownRef = state.GetKnownRef(obj);
            return knownRef ?? cloner(obj, state);
        }
        finally
        {
            state.DecrementDepth();
        }
    }
    
    internal static object? CloneClassShallowAndTrack(object? obj, FastCloneState state)
    {
        if (obj is null)
        {
            return null;
        }

        Type objType = obj.GetType();

        if (FastClonerCache.IsTypeIgnored(objType))
        {
            return null;
        }
        
        // boxed structs can be mutated and must be deep-cloned
        if (FastClonerSafeTypes.CanReturnSameObject(objType) && !objType.IsValueType())
        {
            return obj;
        }

        object? knownRef = state.GetKnownRef(obj);
        if (knownRef is not null)
        {
            return knownRef;
        }
        
        // internal structure of dictionaries, etc. needs to be rebuilt
        if (RequiresSpecializedCloner(objType))
        {
            Func<object, FastCloneState, object>? specialCloner = (Func<object, FastCloneState, object>?)FastClonerCache.GetOrAddClass(objType, t => GenerateCloner(t, true));
            if (specialCloner is not null)
            {
                return specialCloner(obj, state);
            }
        }
        
        MethodInfo methodInfo = typeof(object).GetPrivateMethod(nameof(MemberwiseClone))!;
        object? shallow = methodInfo.Invoke(obj, null);
        state.AddKnownRef(obj, shallow);

        if (state.UseWorkList)
        {
            state.EnqueueProcess(obj, shallow, objType);
        }

        return shallow;
    }
    
    private static bool RequiresSpecializedCloner(Type type)
    {
        return type.IsArray || 
               FastClonerExprGenerator.IsDictionaryType(type) || 
               FastClonerExprGenerator.IsSetType(type);
    }

    internal static T CloneStructInternal<T>(T obj, FastCloneState state)
    {
        Type typeT = typeof(T);
        Type? underlyingTypeT = Nullable.GetUnderlyingType(typeT);
        
        // Check for custom type behaviors
        CloneBehavior? behavior = FastClonerCache.GetTypeBehavior(typeT);
        if (behavior is null && underlyingTypeT is not null)
        {
            behavior = FastClonerCache.GetTypeBehavior(underlyingTypeT);
        }
        
        switch (behavior)
        {
            case CloneBehavior.Ignore:
                return default!;
            case CloneBehavior.Reference:
            case CloneBehavior.Shallow:
                return obj;
            default:
            {
                // no loops, no nulls, no inheritance
                Func<T, FastCloneState, T>? cloner = GetClonerForValueType<T>();

                // safe object
                return cloner is null ? obj : cloner(obj, state);
            }
        }
    }

    // when we can't use code generation, we can use these methods
    internal static T[] Clone1DimArraySafeInternal<T>(T[] obj, FastCloneState state)
    {
        int l = obj.Length;
        T[] outArray = new T[l];
        state.AddKnownRef(obj, outArray);
        Array.Copy(obj, outArray, obj.Length);
        return outArray;
    }

    internal static T[]? Clone1DimArrayStructInternal<T>(T[]? obj, FastCloneState state)
    {
        // not null from called method, but will check it anyway
        if (obj == null) return null;
        int l = obj.Length;
        T[] outArray = new T[l];
        state.AddKnownRef(obj, outArray);
        Func<T, FastCloneState, T> cloner = GetClonerForValueType<T>();
        for (int i = 0; i < l; i++)
            outArray[i] = cloner(obj[i], state);

        return outArray;
    }

    internal static T[]? Clone1DimArrayClassInternal<T>(T[]? obj, FastCloneState state)
    {
        // not null from called method, but will check it anyway
        if (obj == null) return null;
        int l = obj.Length;
        T[] outArray = new T[l];
        state.AddKnownRef(obj, outArray);
        for (int i = 0; i < l; i++)
            outArray[i] = (T)CloneClassInternal(obj[i], state);

        return outArray;
    }

    // relatively frequent case. specially handled
    internal static T[,]? Clone2DimArrayInternal<T>(T[,]? obj, FastCloneState state)
    {
        // not null from called method, but will check it anyway
        if (obj is null)
        {
            return null;
        }

        // we cannot determine by type multidim arrays (one dimension is possible)
        // so, will check for index here
        int lb1 = obj.GetLowerBound(0);
        int lb2 = obj.GetLowerBound(1);
        if (lb1 != 0 || lb2 != 0)
            return (T[,]) CloneAbstractArrayInternal(obj, state);

        int l1 = obj.GetLength(0);
        int l2 = obj.GetLength(1);
        T[,] outArray = new T[l1, l2];
        state.AddKnownRef(obj, outArray);
        if (FastClonerSafeTypes.CanReturnSameObject(typeof(T)))
        {
            Array.Copy(obj, outArray, obj.Length);
            return outArray;
        }

        if (typeof(T).IsValueType())
        {
            Func<T, FastCloneState, T> cloner = GetClonerForValueType<T>();
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    outArray[i, k] = cloner(obj[i, k], state);
        }
        else
        {
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    outArray[i, k] = (T)CloneClassInternal(obj[i, k], state);
        }

        return outArray;
    }

    // rare cases, very slow cloning. currently it's ok
    internal static Array? CloneAbstractArrayInternal(Array? obj, FastCloneState state)
    {
        // not null from called method, but will check it anyway
        if (obj == null) return null;
        int rank = obj.Rank;

        int[] lengths = Enumerable.Range(0, rank).Select(obj.GetLength).ToArray();

        int[] lowerBounds = Enumerable.Range(0, rank).Select(obj.GetLowerBound).ToArray();
        int[] idxes = Enumerable.Range(0, rank).Select(obj.GetLowerBound).ToArray();

        Type? elementType = obj.GetType().GetElementType();
        Array outArray = Array.CreateInstance(elementType, lengths, lowerBounds);

        state.AddKnownRef(obj, outArray);

        // we're unable to set any value to this array, so, just return it
        if (lengths.Any(x => x == 0))
            return outArray;

        if (FastClonerSafeTypes.CanReturnSameObject(elementType))
        {
            Array.Copy(obj, outArray, obj.Length);
            return outArray;
        }

        int ofs = rank - 1;
        while (true)
        {
            outArray.SetValue(CloneClassInternal(obj.GetValue(idxes), state), idxes);
            idxes[ofs]++;

            if (idxes[ofs] >= lowerBounds[ofs] + lengths[ofs])
            {
                do
                {
                    if (ofs == 0) return outArray;
                    idxes[ofs] = lowerBounds[ofs];
                    ofs--;
                    idxes[ofs]++;
                } while (idxes[ofs] >= lowerBounds[ofs] + lengths[ofs]);

                ofs = rank - 1;
            }
        }
    }

    internal static Func<T, FastCloneState, T>? GetClonerForValueType<T>() => (Func<T, FastCloneState, T>?)FastClonerCache.GetOrAddStructAsObject(typeof(T), t => GenerateCloner(t, false));

    private static object? GenerateCloner(Type t, bool asObject)
    {
        if (FastClonerSafeTypes.CanReturnSameObject(t) && asObject && !t.IsValueType())
            return null;

        return FastClonerExprGenerator.GenerateClonerInternal(t, asObject);
    }

    public static object? CloneObjectTo(object? objFrom, object? objTo, bool isDeep)
    {
        if (objTo == null) return null;

        if (objFrom == null)
            throw new ArgumentNullException(nameof(objFrom), "Cannot copy null object to another");
        Type type = objFrom.GetType();
        if (!type.IsInstanceOfType(objTo))
            throw new InvalidOperationException("From object should be derived from From object, but From object has type " + objFrom.GetType().FullName + " and to " + objTo.GetType().FullName);
        if (objFrom is string)
            throw new InvalidOperationException("It is forbidden to clone strings");
        Func<object, object, FastCloneState, object>? cloner = (Func<object, object, FastCloneState, object>?)(isDeep
            ? FastClonerCache.GetOrAddDeepClassTo(type, t => ClonerToExprGenerator.GenerateClonerInternal(t, true))
            : FastClonerCache.GetOrAddShallowClassTo(type, t => ClonerToExprGenerator.GenerateClonerInternal(t, false)));
        
        if (cloner is null)
            return objTo;
        
        FastCloneState state = FastCloneState.Rent();
        try
        {
            object result = cloner(objFrom, objTo, state);
            
            if (isDeep)
            {
                while (state.TryPop(out object from, out object to, out Type workItemType))
                {
                    Func<object, object, FastCloneState, object> clonerTo = (Func<object, object, FastCloneState, object>)FastClonerCache.GetOrAddDeepClassTo(workItemType, t => ClonerToExprGenerator.GenerateClonerInternal(t, true));
                    clonerTo(from, to, state);
                }
            }
            
            return result;
        }
        finally
        {
            FastCloneState.Return(state);
        }
    }
}
