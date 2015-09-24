using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;

namespace FastClone.Internal {
    internal class CloneExpressionBuilder {
        static readonly MethodInfo _ArrayCloneMethodInfo = typeof(Array).GetMethod("Clone");
        static readonly MethodInfo _ArrayGetLengthMethodInfo = typeof(Array).GetMethod("GetLength");
        static readonly MethodInfo _DictionaryAddMethodInfo = typeof(Dictionary<object, object>).GetMethod("Add");
        static readonly MethodInfo _GetUninitializedObjectMethodInfo = typeof(FormatterServices).GetMethod("GetUninitializedObject", BindingFlags.Static | BindingFlags.Public);

        readonly List<Expression> _Expressions = new List<Expression>();
        readonly ParameterExpression _ObjectDictionary = Expression.Parameter(typeof(Dictionary<object, object>), "objectDictionary");
        readonly ParameterExpression _Original = Expression.Parameter(typeof(object), "original");
        readonly Type _Type;
        readonly List<ParameterExpression> _Variables = new List<ParameterExpression>();

        ParameterExpression _Clone;
        ParameterExpression _TypedOriginal;

        internal CloneExpressionBuilder(Type type) { _Type = type; }

        internal Func<object, Dictionary<object, object>, object> CreateTypeCloner() {
            Expression resultExpression;

            if (TypeIsPrimitiveOrString(_Type)) {
                _Expressions.Add(_Original);
                resultExpression = _Expressions[0];
            } else {
                _Expressions.Add(_ObjectDictionary);

                // To access the fields of the original type, we need it to be of the actual type instead of an object, so perform a downcast
                _TypedOriginal = Expression.Variable(_Type);
                _Variables.Add(_TypedOriginal);
                _Expressions.Add(Expression.Assign(_TypedOriginal, Expression.Convert(_Original, _Type)));

                if (_Type.IsArray)
                    CloneArray();
                else
                    CloneObject();

                resultExpression = Expression.Block(_Variables, _Expressions);
            }

            if (_Type.IsValueType)
                resultExpression = Expression.Convert(resultExpression, typeof(object));

            return Expression.Lambda<Func<object, Dictionary<object, object>, object>>(resultExpression, _Original, _ObjectDictionary).Compile();
        }

        void CloneArray() {
            // Arrays need to be cloned element-by-element
            Type elementType = _Type.GetElementType();

            _Expressions.Add(TypeIsPrimitiveOrString(elementType)
                ? GenerateFieldBasedPrimitiveArrayTransferExpressions(_Type, _Original)
                : GenerateFieldBasedComplexArrayTransferExpressions(_Type, elementType, _TypedOriginal, _Variables, _Expressions));
        }

        void CloneObject() {
            // We need a variable to hold the clone because due to the assignments it won't be last in the block when we're finished
            _Clone = Expression.Variable(_Type);
            _Variables.Add(_Clone);

            _Expressions.Add(
                Expression.Block(
                    // create new instance and add to objectDictionary
                    Expression.Assign(_Clone, Expression.Convert(Expression.Call(_GetUninitializedObjectMethodInfo, Expression.Constant(_Type)), _Type)),
                    Expression.Call(_ObjectDictionary, _DictionaryAddMethodInfo, _Original, Expression.Convert(_Clone, typeof(object)))
                    ));

            // Generate the expressions required to transfer the type field by field
            GenerateFieldBasedComplexTypeTransferExpressions(_Type, _TypedOriginal, _Clone, _Expressions);
            // Make sure the clone is the last thing in the block to set the return value
            _Expressions.Add(_Clone);
        }

        static bool TypeIsPrimitiveOrString(Type type) { return type.IsPrimitive || (type == typeof(string)); }

        /// <summary>
        /// Generates state transfer expressions to copy an array of primitive types
        /// </summary>
        /// <param name="elementType">Type of array that will be cloned</param>
        /// <param name="source">Variable expression for the original array</param>
        /// <returns>The variable holding the cloned array</returns>
        static Expression GenerateFieldBasedPrimitiveArrayTransferExpressions(Type elementType, Expression source) { return Expression.Convert(Expression.Call(Expression.Convert(source, typeof(Array)), _ArrayCloneMethodInfo), elementType); }

        /// <summary>
        /// Generates state transfer expressions to copy an array of complex types
        /// </summary>
        /// <param name="arrayType">Type of array that will be cloned</param>
        /// <param name="elementType">Type of the elements of the array</param>
        /// <param name="originalArray">Variable expression for the original array</param>
        /// <param name="arrayVariables">Receives variables used by the transfer expressions</param>
        /// <param name="arrayExpressions">Receives the generated transfer expressions</param>
        /// <returns>The variable holding the cloned array</returns>
        ParameterExpression GenerateFieldBasedComplexArrayTransferExpressions(Type arrayType, Type elementType, Expression originalArray, ICollection<ParameterExpression> arrayVariables, ICollection<Expression> arrayExpressions) {
            // We need a temporary variable in order to transfer the elements of the array
            ParameterExpression arrayClone = Expression.Variable(arrayType);
            arrayVariables.Add(arrayClone);

            int dimensionCount = arrayType.GetArrayRank();

            List<ParameterExpression> lengths = new List<ParameterExpression>();
            List<ParameterExpression> indexes = new List<ParameterExpression>();
            List<LabelTarget> labels = new List<LabelTarget>();

            // Retrieve the length of each of the array's dimensions
            for (int index = 0; index < dimensionCount; ++index) {
                // Obtain the length of the array in the current dimension
                lengths.Add(Expression.Variable(typeof(int)));
                arrayVariables.Add(lengths[index]);
                arrayExpressions.Add(Expression.Assign(lengths[index], Expression.Call(originalArray, _ArrayGetLengthMethodInfo, Expression.Constant(index))));

                // Set up a variable to index the array in this dimension
                indexes.Add(Expression.Variable(typeof(int)));
                arrayVariables.Add(indexes[index]);

                // Also set up a label than can be used to break out of the dimension's transfer loop
                labels.Add(Expression.Label());
            }

            // Create a new (empty) array with the same dimensions and lengths as the original
            arrayExpressions.Add(Expression.Assign(arrayClone, Expression.NewArrayBounds(elementType, lengths)));

            // Initialize the indexer of the outer loop (indexers are initialized one up
            // in the loops (ie. before the loop using it begins), so we have to set this
            // one outside of the loop building code.
            arrayExpressions.Add(Expression.Assign(indexes[0], Expression.Constant(0)));

            // Build the nested loops (one for each dimension) from the inside out
            Expression innerLoop = null;
            for (int index = dimensionCount - 1; index >= 0; --index) {
                List<ParameterExpression> loopVariables = new List<ParameterExpression>();
                List<Expression> loopExpressions = new List<Expression> { Expression.IfThen(Expression.GreaterThanOrEqual(indexes[index], lengths[index]), Expression.Break(labels[index])) };

                // If we reached the end of the current array dimension, break the loop

                if (innerLoop == null) // The innermost loop clones an actual array element
                    if (TypeIsPrimitiveOrString(elementType))
                        loopExpressions.Add(Expression.Assign(Expression.ArrayAccess(arrayClone, indexes), Expression.ArrayAccess(originalArray, indexes)));
                    else if (elementType.IsValueType)
                        GenerateFieldBasedComplexTypeTransferExpressions(elementType, Expression.ArrayAccess(originalArray, indexes), Expression.ArrayAccess(arrayClone, indexes), loopExpressions);
                    else {
                        List<ParameterExpression> nestedVariables = new List<ParameterExpression>();
                        List<Expression> nestedExpressions = new List<Expression>();

                        // A nested array should be cloned by directly creating a new array (not invoking a cloner) since you cannot derive from an array
                        if (elementType.IsArray) {
                            Type nestedElementType = elementType.GetElementType();
                            Expression clonedElement = TypeIsPrimitiveOrString(nestedElementType)
                                ? GenerateFieldBasedPrimitiveArrayTransferExpressions(elementType, Expression.ArrayAccess(originalArray, indexes))
                                : GenerateFieldBasedComplexArrayTransferExpressions(elementType, nestedElementType, Expression.ArrayAccess(originalArray, indexes), nestedVariables, nestedExpressions);

                            nestedExpressions.Add(Expression.Assign(Expression.ArrayAccess(arrayClone, indexes), clonedElement));
                        } else
                            nestedExpressions.Add(CloneExpressionHelper.CreateCopyComplexArrayTypeFieldExpression(Expression.ArrayAccess(originalArray, indexes), Expression.ArrayAccess(arrayClone, indexes), elementType, _ObjectDictionary));

                        // Whether array-in-array of reference-type-in-array, we need a null check before // doing anything to avoid NullReferenceExceptions for unset members
                        loopExpressions.Add(
                            Expression.IfThen(
                                Expression.NotEqual(Expression.ArrayAccess(originalArray, indexes),
                                    Expression.Constant(null)),
                                Expression.Block(nestedVariables, nestedExpressions)));
                    } else {
                    // Outer loops of any level just reset the inner loop's indexer and execute the inner loop
                    loopExpressions.Add(Expression.Assign(indexes[index + 1], Expression.Constant(0)));
                    loopExpressions.Add(innerLoop);
                }

                // Each time we executed the loop instructions, increment the indexer
                loopExpressions.Add(Expression.PreIncrementAssign(indexes[index]));

                // Build the loop using the expressions recorded above
                innerLoop = Expression.Loop(Expression.Block(loopVariables, loopExpressions), labels[index]);
            }

            // After the loop builder has finished, the innerLoop variable contains the entire hierarchy of nested loops, so add this to the clone expressions.
            arrayExpressions.Add(innerLoop);

            return arrayClone;
        }

        /// <summary>
        /// Generates state transfer expressions to copy a complex type
        /// </summary>
        /// <param name="complexType">Complex type that will be cloned</param>
        /// <param name="source">Variable expression for the original instance</param>
        /// <param name="target">Variable expression for the cloned instance</param>
        /// <param name="expression">Receives the generated transfer expressions</param>
        void GenerateFieldBasedComplexTypeTransferExpressions(Type complexType, Expression source, Expression target, ICollection<Expression> expression) {
            // Enumerate all of the type's fields and generate transfer expressions for each
            FieldInfo[] fieldInfos = GetFieldInfosIncludingBaseClasses(complexType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (FieldInfo fieldInfo in fieldInfos) {
                Type fieldType = fieldInfo.FieldType;

                if (TypeIsPrimitiveOrString(fieldType))
                    expression.Add(CloneExpressionHelper.CreateCopyFieldExpression(source, target, fieldInfo));
                else if (fieldType.IsValueType) // A nested value type is part of the parent and will have its fields directly assigned without boxing, new instance creation or anything like that.
                    GenerateFieldBasedComplexTypeTransferExpressions(fieldType, Expression.Field(source, fieldInfo), Expression.Field(target, fieldInfo), expression);
                else
                    GenerateFieldBasedReferenceTypeTransferExpressions(source, target, expression, fieldInfo);
            }
        }

        /// <summary>
        /// Generates the expressions to transfer a reference type (array or class)
        /// </summary>
        /// <param name="original">Original value that will be cloned</param>
        /// <param name="clone">Variable that will receive the cloned value</param>
        /// <param name="expressions">
        /// Receives the expression generated to transfer the values
        /// </param>
        /// <param name="fieldInfo">Reflection informations about the field being cloned</param>
        void GenerateFieldBasedReferenceTypeTransferExpressions(Expression original, Expression clone, ICollection<Expression> expressions, FieldInfo fieldInfo) {
            // Reference types and arrays require special care because they can be null, so gather the transfer expressions in a separate block for the null check
            List<Expression> fieldExpressions = new List<Expression>();
            List<ParameterExpression> fieldVariables = new List<ParameterExpression>();

            Type fieldType = fieldInfo.FieldType;

            if (fieldType.IsArray) {
                Expression fieldClone = GenerateFieldBasedComplexArrayTransferExpressions(fieldType, fieldType.GetElementType(), Expression.Field(original, fieldInfo), fieldVariables, fieldExpressions);
                fieldExpressions.Add(CloneExpressionHelper.CreateSetFieldExpression(clone, fieldClone, fieldInfo));
            } else
                fieldExpressions.Add(CloneExpressionHelper.CreateCopyComplexFieldExpression(original, clone, fieldInfo, _ObjectDictionary));

            expressions.Add(
                Expression.IfThen(
                    Expression.NotEqual(Expression.Field(original, fieldInfo), Expression.Constant(null)),
                    Expression.Block(fieldVariables, fieldExpressions))
                );
        }

        /// <summary>
        /// Returns all the fields of a type, working around a weird reflection issue
        /// where explicitly declared fields in base classes are returned, but not
        /// automatic property backing fields.
        /// </summary>
        /// <param name="type">Type whose fields will be returned</param>
        /// <param name="bindingFlags">Binding flags to use when querying the fields</param>
        /// <returns>All of the type's fields, including its base types</returns>
        public static FieldInfo[] GetFieldInfosIncludingBaseClasses(Type type, BindingFlags bindingFlags) {
            FieldInfo[] fieldInfos = type.GetFields(bindingFlags);

            // If this class doesn't have a base, don't waste any time
            if (type.BaseType == typeof(object))
                return fieldInfos;

            // Otherwise, collect all types up to the furthest base class
            List<FieldInfo> fieldInfoList = new List<FieldInfo>(fieldInfos);
            while (type != null && type.BaseType != typeof(object)) {
                type = type.BaseType;
                if (type != null) {
                    fieldInfos = type.GetFields(bindingFlags);

                    // Look for fields we do not have listed yet and merge them into the main list
                    foreach (FieldInfo fieldInfo in fieldInfos)
                        if (!fieldInfoList.Any(x => x.DeclaringType == fieldInfo.DeclaringType && x.Name == fieldInfo.Name))
                            fieldInfoList.Add(fieldInfo);
                }
            }

            return fieldInfoList.ToArray();
        }
    }
}