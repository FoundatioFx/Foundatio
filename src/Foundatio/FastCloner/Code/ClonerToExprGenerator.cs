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

internal static class ClonerToExprGenerator
{
    internal static object GenerateClonerInternal(Type realType, bool isDeepClone)
    {
        return realType.IsValueType() ? throw new InvalidOperationException("Operation is valid only for reference types") : GenerateProcessMethod(realType, isDeepClone);
    }

    private static object GenerateProcessMethod(Type type, bool isDeepClone)
    {
        if (type.IsArray)
        {
            return GenerateProcessArrayMethod(type, isDeepClone);
        }

        Type methodType = typeof(object);

        List<Expression> expressionList = [];

        ParameterExpression from = Expression.Parameter(methodType);
        ParameterExpression fromLocal = from;
        ParameterExpression to = Expression.Parameter(methodType);
        ParameterExpression toLocal = to;
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));

        // if (!type.IsValueType())
        {
            fromLocal = Expression.Variable(type);
            toLocal = Expression.Variable(type);
            // fromLocal = (T)from
            expressionList.Add(Expression.Assign(fromLocal, Expression.Convert(from, type)));
            expressionList.Add(Expression.Assign(toLocal, Expression.Convert(to, type)));

            if (isDeepClone)
            {
                // added from -> to binding to ensure reference loop handling
                // structs cannot loop here
                // state.AddKnownRef(from, to)
                expressionList.Add(Expression.Call(state, typeof(FastCloneState).GetMethod(nameof(FastCloneState.AddKnownRef))!, from, to));
            }
        }

        List<FieldInfo> fi = [];
        Type? tp = type;
        do
        {
            if (tp == typeof(ContextBoundObject))
            {
                break;
            }

            fi.AddRange(tp.GetDeclaredFields());
            tp = tp.BaseType();
        }
        while (tp != null);

        foreach (FieldInfo fieldInfo in fi)
        {
            // Check if member should be shallow cloned (copy reference directly)
            if (FastClonerExprGenerator.MemberIsShallow(fieldInfo))
            {
                Expression sourceValue = Expression.Field(fromLocal, fieldInfo);
                if (fieldInfo.IsInitOnly)
                {
                    ConstantExpression setter = Expression.Constant(FieldAccessorGenerator.GetFieldSetter(fieldInfo));
                    expressionList.Add(
                        Expression.Invoke(
                            setter,
                            Expression.Convert(toLocal, typeof(object)),
                            Expression.Convert(sourceValue, typeof(object))
                        )
                    );
                }
                else
                {
                    expressionList.Add(Expression.Assign(Expression.Field(toLocal, fieldInfo), sourceValue));
                }
            }
            else if (isDeepClone && !FastClonerSafeTypes.CanReturnSameObject(fieldInfo.FieldType))
            {
                MethodInfo methodInfo = fieldInfo.FieldType.IsValueType()
                    ? StaticMethodInfos.DeepClonerGeneratorMethods.CloneStructInternal.MakeGenericMethod(fieldInfo.FieldType)
                    : StaticMethodInfos.DeepClonerGeneratorMethods.CloneClassInternal;

                MemberExpression get = Expression.Field(fromLocal, fieldInfo);

                // toLocal.Field = Clone...Internal(fromLocal.Field)
                Expression call = Expression.Call(methodInfo, get, state);
                if (!fieldInfo.FieldType.IsValueType())
                    call = Expression.Convert(call, fieldInfo.FieldType);

                if (fieldInfo.IsInitOnly)
                {
                    ConstantExpression setter = Expression.Constant(FieldAccessorGenerator.GetFieldSetter(fieldInfo));
                    expressionList.Add(
                        Expression.Invoke(
                            setter,
                            Expression.Convert(toLocal, typeof(object)),
                            Expression.Convert(call, typeof(object))
                        )
                    );
                }
                else
                {
                    expressionList.Add(Expression.Assign(Expression.Field(toLocal, fieldInfo), call));
                }
            }
            else
            {
                Expression sourceValue = Expression.Field(fromLocal, fieldInfo);
                if (fieldInfo.IsInitOnly)
                {
                    ConstantExpression setter = Expression.Constant(FieldAccessorGenerator.GetFieldSetter(fieldInfo));
                    expressionList.Add(
                        Expression.Invoke(
                            setter,
                            Expression.Convert(toLocal, typeof(object)),
                            Expression.Convert(sourceValue, typeof(object))
                        )
                    );
                }
                else
                {
                    expressionList.Add(Expression.Assign(Expression.Field(toLocal, fieldInfo), sourceValue));
                }
            }
        }

        expressionList.Add(Expression.Convert(toLocal, methodType));

        Type funcType = typeof(Func<,,,>).MakeGenericType(methodType, methodType, typeof(FastCloneState), methodType);

        List<ParameterExpression> blockParams = [];
        if (from != fromLocal) blockParams.Add(fromLocal);
        if (to != toLocal) blockParams.Add(toLocal);

        return Expression.Lambda(funcType, Expression.Block(blockParams, expressionList), from, to, state).Compile();
    }

    private static object GenerateProcessArrayMethod(Type type, bool isDeep)
    {
        Type elementType = type.GetElementType()!;
        int rank = type.GetArrayRank();

        ParameterExpression from = Expression.Parameter(typeof(object));
        ParameterExpression to = Expression.Parameter(typeof(object));
        ParameterExpression state = Expression.Parameter(typeof(FastCloneState));

        Type funcType = typeof(Func<,,,>).MakeGenericType(typeof(object), typeof(object), typeof(FastCloneState), typeof(object));

        if (rank == 1 && type == elementType.MakeArrayType())
        {
            if (!isDeep)
            {
                MethodCallExpression callS = Expression.Call(
                    typeof(ClonerToExprGenerator).GetPrivateStaticMethod(nameof(ShallowClone1DimArraySafeInternal))!
                                                                                    .MakeGenericMethod(elementType), Expression.Convert(from, type), Expression.Convert(to, type));
                return Expression.Lambda(funcType, callS, from, to, state).Compile();
            }
            else
            {
                string methodName = nameof(Clone1DimArrayClassInternal);
                if (FastClonerSafeTypes.CanReturnSameObject(elementType)) methodName = nameof(Clone1DimArraySafeInternal);
                else if (elementType.IsValueType()) methodName = nameof(Clone1DimArrayStructInternal);
                MethodInfo methodInfo = typeof(ClonerToExprGenerator).GetPrivateStaticMethod(methodName)!.MakeGenericMethod(elementType);
                MethodCallExpression callS = Expression.Call(methodInfo, Expression.Convert(from, type), Expression.Convert(to, type), state);
                return Expression.Lambda(funcType, callS, from, to, state).Compile();
            }
        }
        else
        {
            // multidim or not zero-based arrays
            MethodInfo methodInfo;
            if (rank == 2 && type == elementType.MakeArrayType(2))
                methodInfo = typeof(ClonerToExprGenerator).GetPrivateStaticMethod(nameof(Clone2DimArrayInternal))!.MakeGenericMethod(elementType);
            else
                methodInfo = typeof(ClonerToExprGenerator).GetPrivateStaticMethod(nameof(CloneAbstractArrayInternal))!;

            MethodCallExpression callS = Expression.Call(methodInfo, Expression.Convert(from, type), Expression.Convert(to, type), state, Expression.Constant(isDeep));
            return Expression.Lambda(funcType, callS, from, to, state).Compile();
        }
    }

    // when we can't use code generation, we can use these methods
    internal static T[] ShallowClone1DimArraySafeInternal<T>(T[] objFrom, T[] objTo)
    {
        int l = Math.Min(objFrom.Length, objTo.Length);
        Array.Copy(objFrom, objTo, l);
        return objTo;
    }

    // when we can't use code generation, we can use these methods
    internal static T[] Clone1DimArraySafeInternal<T>(T[] objFrom, T[] objTo, FastCloneState state)
    {
        int l = Math.Min(objFrom.Length, objTo.Length);
        state.AddKnownRef(objFrom, objTo);
        Array.Copy(objFrom, objTo, l);
        return objTo;
    }

    internal static T[]? Clone1DimArrayStructInternal<T>(T[]? objFrom, T[]? objTo, FastCloneState state)
    {
        // not null from called method, but will check it anyway
        if (objFrom == null || objTo == null) return null;
        int l = Math.Min(objFrom.Length, objTo.Length);
        state.AddKnownRef(objFrom, objTo);
        Func<T, FastCloneState, T>? cloner = FastClonerGenerator.GetClonerForValueType<T>();

        if (cloner is not null)
        {
            for (int i = 0; i < l; i++)
            {
                objTo[i] = cloner(objTo[i], state);
            }   
        }

        return objTo;
    }

    internal static T[]? Clone1DimArrayClassInternal<T>(T[]? objFrom, T[]? objTo, FastCloneState state)
    {
        // not null from called method, but will check it anyway
        if (objFrom == null || objTo == null) return null;
        int l = Math.Min(objFrom.Length, objTo.Length);
        state.AddKnownRef(objFrom, objTo);
        for (int i = 0; i < l; i++)
            objTo[i] = (T)FastClonerGenerator.CloneClassInternal(objFrom[i], state)!;

        return objTo;
    }

    internal static T[,]? Clone2DimArrayInternal<T>(T[,]? objFrom, T[,]? objTo, FastCloneState state, bool isDeep)
    {
        // not null from called method, but will check it anyway
        if (objFrom == null || objTo == null) return null;
        if (objFrom.GetLowerBound(0) != 0 || objFrom.GetLowerBound(1) != 0
                                          || objTo.GetLowerBound(0) != 0 || objTo.GetLowerBound(1) != 0)
            return (T[,]?) CloneAbstractArrayInternal(objFrom, objTo, state, isDeep);

        int l1 = Math.Min(objFrom.GetLength(0), objTo.GetLength(0));
        int l2 = Math.Min(objFrom.GetLength(1), objTo.GetLength(1));
        state.AddKnownRef(objFrom, objTo);
        if ((!isDeep || FastClonerSafeTypes.CanReturnSameObject(typeof(T)))
            && objFrom.GetLength(0) == objTo.GetLength(0)
            && objFrom.GetLength(1) == objTo.GetLength(1))
        {
            Array.Copy(objFrom, objTo, objFrom.Length);
            return objTo;
        }

        if (!isDeep)
        {
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    objTo[i, k] = objFrom[i, k];
            return objTo;
        }

        if (typeof(T).IsValueType())
        {
            Func<T, FastCloneState, T>? cloner = FastClonerGenerator.GetClonerForValueType<T>();

            if (cloner is not null)
            {
                for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    objTo[i, k] = cloner(objFrom[i, k], state);
            }
        }
        else
        {
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    objTo[i, k] = (T)FastClonerGenerator.CloneClassInternal(objFrom[i, k], state)!;
        }

        return objTo;
    }

    // rare cases, very slow cloning. currently it's ok
    internal static Array? CloneAbstractArrayInternal(Array? objFrom, Array? objTo, FastCloneState state, bool isDeep)
    {
        // not null from called method, but will check it anyway
        if (objFrom == null || objTo == null) return null;
        int rank = objFrom.Rank;

        if (objTo.Rank != rank)
            throw new InvalidOperationException("Invalid rank of target array");
        int[] lowerBoundsFrom = Enumerable.Range(0, rank).Select(objFrom.GetLowerBound).ToArray();
        int[] lowerBoundsTo = Enumerable.Range(0, rank).Select(objTo.GetLowerBound).ToArray();
        int[] lengths = Enumerable.Range(0, rank).Select(x => Math.Min(objFrom.GetLength(x), objTo.GetLength(x))).ToArray();
        int[] idxesFrom = Enumerable.Range(0, rank).Select(objFrom.GetLowerBound).ToArray();
        int[] idxesTo = Enumerable.Range(0, rank).Select(objTo.GetLowerBound).ToArray();

        state.AddKnownRef(objFrom, objTo);

        // unable to copy any element
        if (lengths.Any(x => x == 0))
            return objTo;

        while (true)
        {
            objTo.SetValue(
                isDeep
                    ? FastClonerGenerator.CloneClassInternal(
                        objFrom.GetValue(idxesFrom),
                        state)
                    : objFrom.GetValue(idxesFrom), idxesTo);
            int ofs = rank - 1;
            while (true)
            {
                idxesFrom[ofs]++;
                idxesTo[ofs]++;
                if (idxesFrom[ofs] >= lowerBoundsFrom[ofs] + lengths[ofs])
                {
                    idxesFrom[ofs] = lowerBoundsFrom[ofs];
                    idxesTo[ofs] = lowerBoundsTo[ofs];
                    ofs--;
                    if (ofs < 0) return objTo;
                }
                else
                    break;
            }
        }
    }
}
