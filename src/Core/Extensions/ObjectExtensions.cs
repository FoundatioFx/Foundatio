using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FastClone.Internal;

namespace Foundatio.Extensions {
    public static class ObjectExtensions {
        public static bool IsPrimitive(this Type type) {
            if (type == typeof(String))
                return true;
            return (type.IsValueType & type.IsPrimitive);
        }

        public static Object Copy(this Object original) {
            if (original == null)
                return null;

            var typeToReflect = original.GetType();
            if (IsPrimitive(typeToReflect))
                return original;

            Func<object, Dictionary<object, object>, object> creator = GetTypeCloner(typeToReflect);
            return creator(original, new Dictionary<object, object>());
        }

        static Func<object, Dictionary<object, object>, object> GetTypeCloner(Type type) { return _TypeCloners.GetOrAdd(type, t => new CloneExpressionBuilder(t).CreateTypeCloner()); }
        static readonly ConcurrentDictionary<Type, Func<object, Dictionary<object, object>, object>> _TypeCloners = new ConcurrentDictionary<Type, Func<object, Dictionary<object, object>, object>>();

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