using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace FastClone.Internal {
    internal static class CloneExpressionHelper {
        static readonly MethodInfo _fieldInfoSetValueMethod = typeof(FieldInfo).GetMethod("SetValue", new[] { TypeHelper.ObjectType, TypeHelper.ObjectType });
        static readonly MethodInfo _dictionaryContainsKey = typeof(Dictionary<object, object>).GetMethod("ContainsKey");
        static readonly MethodInfo _dictionaryGetItem = typeof(Dictionary<object, object>).GetMethod("get_Item");
        static readonly MethodInfo _getTypeClonerMethodInfo = typeof(ObjectExtensions).GetMethod("GetTypeCloner", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo _getTypeMethodInfo = TypeHelper.ObjectType.GetMethod("GetType");
        static readonly MethodInfo _invokeMethodInfo = typeof(Func<object, Dictionary<object, object>, object>).GetMethod("Invoke");

        /// <summary>
        /// Creates an expression that copies a value from the original to the clone.
        /// </summary>
        /// <param name="original"></param>
        /// <param name="clone"></param>
        /// <param name="fieldInfo"></param>
        /// <returns></returns>
        internal static Expression CreateCopyFieldExpression(Expression original, Expression clone, FieldInfo fieldInfo) {
            return CreateSetFieldExpression(clone, Expression.Field(original, fieldInfo), fieldInfo);
        }

        internal static Expression CreateSetFieldExpression(Expression clone, Expression value, FieldInfo fieldInfo) {
            // workaround for readonly fields: use reflection, this is a lot slower but the only way except using il directly
            if (fieldInfo.IsInitOnly)
                return Expression.Call(Expression.Constant(fieldInfo), _fieldInfoSetValueMethod, clone, Expression.Convert(value, TypeHelper.ObjectType));

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
                Expression.Call(objectDictionary, _dictionaryContainsKey, originalField),
                CreateSetFieldExpression(clone, Expression.Convert(Expression.Call(objectDictionary, _dictionaryGetItem, originalField), fieldInfo.FieldType), fieldInfo),
                CreateSetFieldExpression(clone, Expression.Convert(Expression.Call(Expression.Call(_getTypeClonerMethodInfo, Expression.Call(originalField, _getTypeMethodInfo)), _invokeMethodInfo, originalField, objectDictionary), fieldInfo.FieldType), fieldInfo)
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
                Expression.Call(objectDictionary, _dictionaryContainsKey, sourceField),
                Expression.Assign(targetField, Expression.Convert(Expression.Call(objectDictionary, _dictionaryGetItem, sourceField), type)),
                Expression.Assign(targetField, Expression.Convert(Expression.Call(Expression.Call(_getTypeClonerMethodInfo, Expression.Call(sourceField, _getTypeMethodInfo)), _invokeMethodInfo, sourceField, objectDictionary), type))
                );
        }
    }
}