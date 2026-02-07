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

internal static class ShallowClonerGenerator
{
    public static T? CloneObject<T>(T obj)
    {
        // this is faster than typeof(T).IsValueType
        if (obj is ValueType)
        {
            if (typeof(T) == obj.GetType())
                return obj;

            // we're here so, we clone value type obj as object type T
            // so, we need to copy it, bcs we have a reference, not real object.
            return (T)ShallowObjectCloner.CloneObject(obj);
        }

        if (ReferenceEquals(obj, null))
            return (T?)(object?)null;

        if (FastClonerSafeTypes.CanReturnSameObject(obj.GetType()))
            return obj;

        return (T)ShallowObjectCloner.CloneObject(obj);
    }
}
