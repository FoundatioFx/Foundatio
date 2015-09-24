using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Foundatio.Extensions;

namespace FastClone.Internal {
    internal static class CloneExpressionHelper {
        static readonly MethodInfo _FieldInfoSetValueMethod = typeof(FieldInfo).GetMethod("SetValue", new[] { typeof(object), typeof(object) });
        static readonly MethodInfo _DictionaryContainsKey = typeof(Dictionary<object, object>).GetMethod("ContainsKey");
        static readonly MethodInfo _DictionaryGetItem = typeof(Dictionary<object, object>).GetMethod("get_Item");
        static readonly MethodInfo _GetTypeClonerMethodInfo = typeof(ObjectExtensions).GetMethod("GetTypeCloner", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo _GetTypeMethodInfo = typeof(object).GetMethod("GetType");
        static readonly MethodInfo _InvokeMethodInfo = typeof(Func<object, Dictionary<object, object>, object>).GetMethod("Invoke");

        /// <summary>
        /// Creates an expression that copies a value from the original to the clone.
        /// </summary>
        /// <param name="original"></param>
        /// <param name="clone"></param>
        /// <param name="fieldInfo"></param>
        /// <returns></returns>
        internal static Expression CreateCopyFieldExpression(Expression original, Expression clone, FieldInfo fieldInfo) { return CreateSetFieldExpression(clone, Expression.Field(original, fieldInfo), fieldInfo); }

        internal static Expression CreateSetFieldExpression(Expression clone, Expression value, FieldInfo fieldInfo) {
            // workaround for readonly fields: use reflection, this is a lot slower but the only way except using il directly
            if (fieldInfo.IsInitOnly)
                return Expression.Call(Expression.Constant(fieldInfo), _FieldInfoSetValueMethod, clone, Expression.Convert(value, typeof(object)));

            return Expression.Assign(Expression.Field(clone, fieldInfo), value);
        }

        /// <summary>
        /// Creates an expression that copies a coplex value from the source to the target. The value will be cloned as well using the dictionary to reuse already cloned objects.
        /// </summary>
        /// <param name="original"></param>
        /// <param name="clone"></param>
        /// <param name="fieldInfo"></param>
        /// <param name="objectDictionary"></param>
        /// <returns></returns>
        internal static Expression CreateCopyComplexFieldExpression(Expression original, Expression clone, FieldInfo fieldInfo, ParameterExpression objectDictionary) {
            Expression originalField = Expression.Field(original, fieldInfo);

            return Expression.IfThenElse(
                Expression.Call(objectDictionary, _DictionaryContainsKey, originalField),
                CreateSetFieldExpression(clone, Expression.Convert(Expression.Call(objectDictionary, _DictionaryGetItem, originalField), fieldInfo.FieldType), fieldInfo),
                CreateSetFieldExpression(clone, Expression.Convert(Expression.Call(Expression.Call(_GetTypeClonerMethodInfo, Expression.Call(originalField, _GetTypeMethodInfo)), _InvokeMethodInfo, originalField, objectDictionary), fieldInfo.FieldType), fieldInfo)
                );
        }

        /// <summary>
        /// Creates an expression that copies a coplex array value from the source to the target. The value will be cloned as well using the dictionary to reuse already cloned objects.
        /// </summary>
        /// <param name="sourceField"></param>
        /// <param name="targetField"></param>
        /// <param name="type"></param>
        /// <param name="objectDictionary"></param>
        /// <returns></returns>
        internal static Expression CreateCopyComplexArrayTypeFieldExpression(Expression sourceField, Expression targetField, Type type, ParameterExpression objectDictionary) {
            return Expression.IfThenElse(
                Expression.Call(objectDictionary, _DictionaryContainsKey, sourceField),
                Expression.Assign(targetField, Expression.Convert(Expression.Call(objectDictionary, _DictionaryGetItem, sourceField), type)),
                Expression.Assign(targetField, Expression.Convert(Expression.Call(Expression.Call(_GetTypeClonerMethodInfo, Expression.Call(sourceField, _GetTypeMethodInfo)), _InvokeMethodInfo, sourceField, objectDictionary), type))
                );
        }
    }
}