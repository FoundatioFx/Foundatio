using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using Foundatio.Utility;

namespace Foundatio.Extensions {
    public class ExpressionTreeCloner {
        public static Func<object, object> CreateDeepFieldBasedCloner(Type clonedType) {
            ParameterExpression original = Expression.Parameter(typeof(object), "original");

            var transferExpressions = new List<Expression>();
            var variables = new List<ParameterExpression>();

            if (clonedType.IsPrimitive || (clonedType == typeof(string))) {
                // Primitives and strings are copied on direct assignment
                transferExpressions.Add(original);
            } else if (clonedType.IsArray) {
                // Arrays need to be cloned element-by-element
                Type elementType = clonedType.GetElementType();

                if (elementType.IsPrimitive || (elementType == typeof(string))) {
                    // For primitive arrays, the Array.Clone() method is sufficient
                    transferExpressions.Add(
                        GenerateFieldBasedPrimitiveArrayTransferExpressions(
                            clonedType, original, variables, transferExpressions
                            )
                        );
                } else {
                    // To access the fields of the original type, we need it to be of the actual
                    // type instead of an object, so perform a downcast
                    ParameterExpression typedOriginal = Expression.Variable(clonedType);
                    variables.Add(typedOriginal);
                    transferExpressions.Add(
                        Expression.Assign(typedOriginal, Expression.Convert(original, clonedType))
                        );

                    // Arrays of complex types require manual cloning
                    transferExpressions.Add(
                        GenerateFieldBasedComplexArrayTransferExpressions(
                            clonedType, typedOriginal, variables, transferExpressions
                            )
                        );
                }
            } else {
                // We need a variable to hold the clone because due to the assignments it
                // won't be last in the block when we're finished
                ParameterExpression clone = Expression.Variable(clonedType);
                variables.Add(clone);

                // Give it a new instance of the type being cloned
                MethodInfo getUninitializedObjectMethodInfo = typeof(FormatterServices).GetMethod(
                    "GetUninitializedObject", BindingFlags.Static | BindingFlags.Public
                    );
                transferExpressions.Add(
                    Expression.Assign(
                        clone,
                        Expression.Convert(
                            Expression.Call(
                                getUninitializedObjectMethodInfo, Expression.Constant(clonedType)
                                ),
                            clonedType
                            )
                        )
                    );

                // To access the fields of the original type, we need it to be of the actual
                // type instead of an object, so perform a downcast
                ParameterExpression typedOriginal = Expression.Variable(clonedType);
                variables.Add(typedOriginal);
                transferExpressions.Add(
                    Expression.Assign(typedOriginal, Expression.Convert(original, clonedType))
                    );

                // Generate the expressions required to transfer the type field by field
                GenerateFieldBasedComplexTypeTransferExpressions(
                    clonedType, typedOriginal, clone, variables, transferExpressions
                    );

                // Make sure the clone is the last thing in the block to set the return value
                transferExpressions.Add(clone);
            }

            // Turn all transfer expressions into a single block if necessary
            Expression resultExpression;
            if ((transferExpressions.Count == 1) && (variables.Count == 0)) {
                resultExpression = transferExpressions[0];
            } else {
                resultExpression = Expression.Block(variables, transferExpressions);
            }

            // Value types require manual boxing
            if (clonedType.IsValueType) {
                resultExpression = Expression.Convert(resultExpression, typeof(object));
            }

            return Expression.Lambda<Func<object, object>>(resultExpression, original).Compile();
        }

        private static Expression GenerateFieldBasedPrimitiveArrayTransferExpressions(
            Type clonedType,
            Expression original,
            ICollection<ParameterExpression> variables,
            ICollection<Expression> transferExpressions
            ) {
            MethodInfo arrayCloneMethodInfo = typeof(Array).GetMethod("Clone");
            return Expression.Convert(
                Expression.Call(
                    Expression.Convert(original, typeof(Array)), arrayCloneMethodInfo
                    ),
                clonedType
                );
        }

        private static ParameterExpression GenerateFieldBasedComplexArrayTransferExpressions(
            Type clonedType,
            Expression original,
            IList<ParameterExpression> variables,
            ICollection<Expression> transferExpressions
            ) {
            // We need a temporary variable in order to transfer the elements of the array
            ParameterExpression clone = Expression.Variable(clonedType);
            variables.Add(clone);

            int dimensionCount = clonedType.GetArrayRank();
            Type elementType = clonedType.GetElementType();

            var lengths = new List<ParameterExpression>();
            var indexes = new List<ParameterExpression>();
            var labels = new List<LabelTarget>();

            // Retrieve the length of each of the array's dimensions
            MethodInfo arrayGetLengthMethodInfo = typeof(Array).GetMethod("GetLength");
            for (int index = 0; index < dimensionCount; ++index) {
                // Obtain the length of the array in the current dimension
                lengths.Add(Expression.Variable(typeof(int)));
                variables.Add(lengths[index]);
                transferExpressions.Add(
                    Expression.Assign(
                        lengths[index],
                        Expression.Call(
                            original, arrayGetLengthMethodInfo, Expression.Constant(index)
                            )
                        )
                    );

                // Set up a variable to index the array in this dimension
                indexes.Add(Expression.Variable(typeof(int)));
                variables.Add(indexes[index]);

                // Also set up a label than can be used to break out of the dimension's
                // transfer loop
                labels.Add(Expression.Label());
            }

            // Create a new (empty) array with the same dimensions and lengths as the original
            transferExpressions.Add(
                Expression.Assign(clone, Expression.NewArrayBounds(elementType, lengths))
                );

            // Initialize the indexer of the outer loop (indexers are initialized one up
            // in the loops (ie. before the loop using it begins), so we have to set this
            // one outside of the loop building code.
            transferExpressions.Add(
                Expression.Assign(indexes[0], Expression.Constant(0))
                );

            // Build the nested loops (one for each dimension) from the inside out
            Expression innerLoop = null;
            for (int index = dimensionCount - 1; index >= 0; --index) {
                var loopVariables = new List<ParameterExpression>();
                var loopExpressions = new List<Expression>();

                // If we reached the end of the current array dimension, break the loop
                loopExpressions.Add(
                    Expression.IfThen(
                        Expression.GreaterThanOrEqual(indexes[index], lengths[index]),
                        Expression.Break(labels[index])
                        )
                    );

                if (innerLoop == null) {
                    // The innermost loop clones an actual array element

                    if (elementType.IsPrimitive || (elementType == typeof(string))) {
                        // Primitive array elements can be copied by simple assignment. This case
                        // should not occur since Array.Clone() should be used instead.
                        loopExpressions.Add(
                            Expression.Assign(
                                Expression.ArrayAccess(clone, indexes),
                                Expression.ArrayAccess(original, indexes)
                                )
                            );
                    } else if (elementType.IsValueType) {
                        // Arrays of complex value types can be transferred by assigning all fields
                        // of the source array element to the destination array element (cloning
                        // any nested reference types appropriately)
                        GenerateFieldBasedComplexTypeTransferExpressions(
                            elementType,
                            Expression.ArrayAccess(original, indexes),
                            Expression.ArrayAccess(clone, indexes),
                            variables,
                            loopExpressions
                            );
                    } else {
                        // Arrays of reference types need to be cloned by creating a new instance
                        // of the reference type and then transferring the fields over
                        ParameterExpression originalElement = Expression.Variable(elementType);
                        loopVariables.Add(originalElement);

                        loopExpressions.Add(
                            Expression.Assign(originalElement, Expression.ArrayAccess(original, indexes))
                            );

                        var nestedVariables = new List<ParameterExpression>();
                        var nestedTransferExpressions = new List<Expression>();

                        // A nested array should be cloned by directly creating a new array (not invoking
                        // a cloner) since you cannot derive from an array
                        if (elementType.IsArray) {
                            Expression clonedElement;

                            Type nestedElementType = elementType.GetElementType();
                            if (nestedElementType.IsPrimitive || (nestedElementType == typeof(string))) {
                                clonedElement = GenerateFieldBasedPrimitiveArrayTransferExpressions(
                                    elementType, originalElement, nestedVariables, nestedTransferExpressions
                                    );
                            } else {
                                clonedElement = GenerateFieldBasedComplexArrayTransferExpressions(
                                    elementType, originalElement, nestedVariables, nestedTransferExpressions
                                    );
                            }
                            nestedTransferExpressions.Add(
                                Expression.Assign(Expression.ArrayAccess(clone, indexes), clonedElement)
                                );
                        } else {
                            // Complex types are cloned by checking their actual, concrete type (fields
                            // may be typed to an interface or base class) and requesting a cloner for that
                            // type during runtime
                            MethodInfo getOrCreateClonerMethodInfo = typeof(ExpressionTreeCloner).GetMethod(
                                "GetOrCreateDeepFieldBasedCloner",
                                BindingFlags.NonPublic | BindingFlags.Static
                                );
                            MethodInfo getTypeMethodInfo = typeof(object).GetMethod("GetType");
                            MethodInfo invokeMethodInfo = typeof(Func<object, object>).GetMethod("Invoke");

                            // Generate expressions to do this:
                            //   clone.SomeField = getOrCreateDeepFieldBasedCloner(
                            //     original.SomeField.GetType()
                            //   ).Invoke(original.SomeField);
                            nestedTransferExpressions.Add(
                                Expression.Assign(
                                    Expression.ArrayAccess(clone, indexes),
                                    Expression.Convert(
                                        Expression.Call(
                                            Expression.Call(
                                                getOrCreateClonerMethodInfo,
                                                Expression.Call(originalElement, getTypeMethodInfo)
                                                ),
                                            invokeMethodInfo,
                                            originalElement
                                            ),
                                        elementType
                                        )
                                    )
                                );
                        }

                        // Whether array-in-array of reference-type-in-array, we need a null check before
                        // doing anything to avoid NullReferenceExceptions for unset members
                        loopExpressions.Add(
                            Expression.IfThen(
                                Expression.NotEqual(originalElement, Expression.Constant(null)),
                                Expression.Block(
                                    nestedVariables,
                                    nestedTransferExpressions
                                    )
                                )
                            );
                    }
                } else {
                    // Outer loops of any level just reset the inner loop's indexer and execute
                    // the inner loop
                    loopExpressions.Add(
                        Expression.Assign(indexes[index + 1], Expression.Constant(0))
                        );
                    loopExpressions.Add(innerLoop);
                }

                // Each time we executed the loop instructions, increment the indexer
                loopExpressions.Add(Expression.PreIncrementAssign(indexes[index]));

                // Build the loop using the expressions recorded above
                innerLoop = Expression.Loop(
                    Expression.Block(loopVariables, loopExpressions),
                    labels[index]
                    );
            }

            // After the loop builder has finished, the innerLoop variable contains
            // the entire hierarchy of nested loops, so add this to the clone expressions.
            transferExpressions.Add(innerLoop);

            return clone;
        }

        private static void GenerateFieldBasedComplexTypeTransferExpressions(
            Type clonedType, // Actual, concrete type (not declared type)
            Expression original, // Expected to be an object
            Expression clone, // As actual, concrete type
            IList<ParameterExpression> variables,
            ICollection<Expression> transferExpressions
            ) {
            // Enumerate all of the type's fields and generate transfer expressions for each
            FieldInfo[] fieldInfos = ClonerHelpers.GetFieldInfosIncludingBaseClasses(
                clonedType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                );
            for (int index = 0; index < fieldInfos.Length; ++index) {
                FieldInfo fieldInfo = fieldInfos[index];
                Type fieldType = fieldInfo.FieldType;

                if (fieldType.IsPrimitive || (fieldType == typeof(string))) {
                    // Primitive types and strings can be transferred by simple assignment
                    transferExpressions.Add(
                        Expression.Assign(
                            Expression.Field(clone, fieldInfo),
                            Expression.Field(original, fieldInfo)
                            )
                        );
                } else if (fieldType.IsValueType) {
                    // A nested value type is part of the parent and will have its fields directly
                    // assigned without boxing, new instance creation or anything like that.
                    GenerateFieldBasedComplexTypeTransferExpressions(
                        fieldType,
                        Expression.Field(original, fieldInfo),
                        Expression.Field(clone, fieldInfo),
                        variables,
                        transferExpressions
                        );
                } else {
                    GenerateFieldBasedReferenceTypeTransferExpressions(
                        original, clone, transferExpressions, fieldInfo, fieldType
                        );
                }
            }
        }

        private static void GenerateFieldBasedReferenceTypeTransferExpressions(
            Expression original,
            Expression clone,
            ICollection<Expression> transferExpressions,
            FieldInfo fieldInfo,
            Type fieldType
            ) {
            // Reference types and arrays require special care because they can be null,
            // so gather the transfer expressions in a separate block for the null check
            var fieldTransferExpressions = new List<Expression>();
            var fieldVariables = new List<ParameterExpression>();

            if (fieldType.IsArray) {
                // Arrays need to be cloned element-by-element
                Expression fieldClone;

                Type elementType = fieldType.GetElementType();
                if (elementType.IsPrimitive || (elementType == typeof(string))) {
                    // For primitive arrays, the Array.Clone() method is sufficient
                    fieldClone = GenerateFieldBasedPrimitiveArrayTransferExpressions(
                        fieldType,
                        Expression.Field(original, fieldInfo),
                        fieldVariables,
                        fieldTransferExpressions
                        );
                } else {
                    // Arrays of complex types require manual cloning
                    fieldClone = GenerateFieldBasedComplexArrayTransferExpressions(
                        fieldType,
                        Expression.Field(original, fieldInfo),
                        fieldVariables,
                        fieldTransferExpressions
                        );
                }

                // Add the assignment to the transfer expressions. The array transfer expression
                // generator will either have set up a temporary variable to hold the array or
                // returned the conversion expression straight away
                fieldTransferExpressions.Add(
                    Expression.Assign(Expression.Field(clone, fieldInfo), fieldClone)
                    );
            } else {
                // Complex types are cloned by checking their actual, concrete type (fields
                // may be typed to an interface or base class) and requesting a cloner for that
                // type during runtime
                MethodInfo getOrCreateClonerMethodInfo = typeof(ExpressionTreeCloner).GetMethod(
                    "GetOrCreateDeepFieldBasedCloner",
                    BindingFlags.NonPublic | BindingFlags.Static
                    );
                MethodInfo getTypeMethodInfo = typeof(object).GetMethod("GetType");
                MethodInfo invokeMethodInfo = typeof(Func<object, object>).GetMethod("Invoke");

                // Generate expressions to do this:
                //   clone.SomeField = getOrCreateDeepFieldBasedCloner(
                //     original.SomeField.GetType()
                //   ).Invoke(original.SomeField);
                fieldTransferExpressions.Add(
                    Expression.Assign(
                        Expression.Field(clone, fieldInfo),
                        Expression.Convert(
                            Expression.Call(
                                Expression.Call(
                                    getOrCreateClonerMethodInfo,
                                    Expression.Call(
                                        Expression.Field(original, fieldInfo), getTypeMethodInfo
                                        )
                                    ),
                                invokeMethodInfo,
                                Expression.Field(original, fieldInfo)
                                ),
                            fieldType
                            )
                        )
                    );
            }

            // Wrap up the generated array or complex reference type transfer expressions
            // in a null check so the field is skipped if it is not holding an instance.
            transferExpressions.Add(
                Expression.IfThen(
                    Expression.NotEqual(
                        Expression.Field(original, fieldInfo), Expression.Constant(null)
                        ),
                    Expression.Block(fieldVariables, fieldTransferExpressions)
                    )
                );
        }
    }

    public static class ObjectExtensions {
        public static bool IsPrimitive(this Type type) {
            if (type == typeof(String))
                return true;
            return (type.IsValueType & type.IsPrimitive);
        }

        public static Object Copy(this Object originalObject) {
            if (originalObject == null)
                return null;
            var typeToReflect = originalObject.GetType();
            if (IsPrimitive(typeToReflect))
                return originalObject;

            var cloner = GetOrCreateDeepFieldBasedCloner(typeToReflect);
            return cloner(originalObject);
        }

        private static Func<object, object> GetOrCreateDeepFieldBasedCloner(Type clonedType) {
            Func<object, object> cloner;

            if (_cloners.TryGetValue(clonedType, out cloner))
                return cloner;

            cloner = ExpressionTreeCloner.CreateDeepFieldBasedCloner(clonedType);
            _cloners.TryAdd(clonedType, cloner);

            return cloner;
        }

        private static readonly ConcurrentDictionary<Type, Func<object, object>> _cloners = new ConcurrentDictionary<Type, Func<object, object>>();

        public static T Copy<T>(this T original) {
            return (T)Copy((Object)original);
        }
    }

    public class ReferenceEqualityComparer : EqualityComparer<Object> {
        public override bool Equals(object x, object y) {
            return ReferenceEquals(x, y);
        }

        public override int GetHashCode(object obj) {
            if (obj == null)
                return 0;
            return obj.GetHashCode();
        }
    }

    namespace ArrayExtensions {
        public static class ArrayExtensions {
            public static void ForEach(this Array array, Action<Array, int[]> action) {
                if (array.LongLength == 0)
                    return;
                ArrayTraverse walker = new ArrayTraverse(array);
                do
                    action(array, walker.Position);
                while (walker.Step());
            }
        }

        internal class ArrayTraverse {
            public int[] Position;
            private int[] maxLengths;

            public ArrayTraverse(Array array) {
                maxLengths = new int[array.Rank];
                for (int i = 0; i < array.Rank; ++i)
                    maxLengths[i] = array.GetLength(i) - 1;

                Position = new int[array.Rank];
            }

            public bool Step() {
                for (int i = 0; i < Position.Length; ++i) {
                    if (Position[i] >= maxLengths[i])
                        continue;

                    Position[i]++;
                    for (int j = 0; j < i; j++)
                        Position[j] = 0;

                    return true;
                }

                return false;
            }
        }
    }

}