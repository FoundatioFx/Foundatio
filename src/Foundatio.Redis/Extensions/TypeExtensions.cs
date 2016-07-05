using System;
using System.ComponentModel;
using System.Reflection;
using Foundatio.Utility;

namespace Foundatio.Extensions {
    internal static class TypeExtensions {
        public static bool IsNumeric(this Type type) {
            if (type.IsArray)
                return false;
            
            if (type == TypeHelper.ByteType ||
                type == TypeHelper.DecimalType ||
                type == TypeHelper.DoubleType ||
                type == TypeHelper.Int16Type ||
                type == TypeHelper.Int32Type ||
                type == TypeHelper.Int64Type ||
                type == TypeHelper.SByteType ||
                type == TypeHelper.SingleType ||
                type == TypeHelper.UInt16Type ||
                type == TypeHelper.UInt32Type ||
                type == TypeHelper.UInt64Type)
                return true;
            
            switch (Type.GetTypeCode(type)) {
                case TypeCode.Byte:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.SByte:
                case TypeCode.Single:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    return true;
            }

            return false;
        }


        public static bool IsNullableNumeric(this Type type) {
            if (type.IsArray)
                return false;

            var t = Nullable.GetUnderlyingType(type);
            return t != null && t.IsNumeric();
        }
    }
}