using System;

namespace Foundatio.Extensions {
    public static class TypeExtensions {
        public static bool IsNullable(this Type type) {
            if (!type.IsValueType)
                return true; // ref-type

            if (Nullable.GetUnderlyingType(type) != null)
                return true; // Nullable<T>

            return false;
        }
    }
}