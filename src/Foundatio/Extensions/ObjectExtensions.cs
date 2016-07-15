using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using FastClone.Internal;
using Foundatio.Utility;

namespace Foundatio.Extensions {
    internal static class ObjectExtensions {
        public static bool IsPrimitive(this Type type) {
            if (type == TypeHelper.StringType)
                return true;

            var typeInfo = type.GetTypeInfo();
            return typeInfo.IsValueType && typeInfo.IsPrimitive;
        }

        public static object Copy(this object original) {
            if (original == null)
                return null;

            var typeToReflect = original.GetType();
            if (IsPrimitive(typeToReflect))
                return original;

            Func<object, Dictionary<object, object>, object> creator = GetTypeCloner(typeToReflect);
            var dict = new Dictionary<object, object>();
            var result = creator(original, dict);
            return result;
        }

        private static Func<object, Dictionary<object, object>, object> GetTypeCloner(Type type) {
            return _typeCloners.GetOrAdd(type, t => new CloneExpressionBuilder(t).CreateTypeCloner());
        }

        private static readonly ConcurrentDictionary<Type, Func<object, Dictionary<object, object>, object>> _typeCloners = new ConcurrentDictionary<Type, Func<object, Dictionary<object, object>, object>>();

        public static T Copy<T>(this T original) {
            return (T)Copy((object)original);
        }
    }
}