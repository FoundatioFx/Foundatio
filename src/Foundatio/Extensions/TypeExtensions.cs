﻿using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Foundatio.Serializer;

namespace Foundatio.Utility;

internal static class TypeExtensions
{
    public static bool IsNumeric(this Type type)
    {
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

        switch (Type.GetTypeCode(type))
        {
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

    public static bool IsNullableNumeric(this Type type)
    {
        if (type.IsArray)
            return false;

        var t = Nullable.GetUnderlyingType(type);
        return t != null && t.IsNumeric();
    }

    public static T ToType<T>(this object value, ISerializer serializer = null)
    {
        var targetType = typeof(T);
        if (value == null)
        {
            try
            {
                return (T)Convert.ChangeType(value, targetType);
            }
            catch
            {
                throw new ArgumentNullException(nameof(value));
            }
        }

        var converter = TypeDescriptor.GetConverter(targetType);
        var valueType = value.GetType();

        if (targetType.IsAssignableFrom(valueType))
            return (T)value;

        var targetTypeInfo = targetType.GetTypeInfo();
        if (targetTypeInfo.IsEnum && (value is string || valueType.GetTypeInfo().IsEnum))
        {
            // attempt to match enum by name.
            if (EnumExtensions.TryEnumIsDefined(targetType, value.ToString()))
            {
                object parsedValue = Enum.Parse(targetType, value.ToString(), false);
                return (T)parsedValue;
            }

            string message = $"The Enum value of '{value}' is not defined as a valid value for '{targetType.FullName}'.";
            throw new ArgumentException(message);
        }

        if (targetTypeInfo.IsEnum && valueType.IsNumeric())
            return (T)Enum.ToObject(targetType, value);

        if (converter.CanConvertFrom(valueType))
        {
            object convertedValue = converter.ConvertFrom(value);
            return (T)convertedValue;
        }

        if (serializer != null && value is byte[] data)
        {
            try
            {
                return serializer.Deserialize<T>(data);
            }
            catch { }
        }

        if (serializer != null && value is string stringValue)
        {
            try
            {
                return serializer.Deserialize<T>(stringValue);
            }
            catch { }
        }

        if (value is IConvertible)
        {
            try
            {
                object convertedValue = Convert.ChangeType(value, targetType);
                return (T)convertedValue;
            }
            catch { }
        }

        throw new ArgumentException($"An incompatible value specified.  Target Type: {targetType.FullName} Value Type: {value.GetType().FullName}", nameof(value));
    }

    public static string GetFriendlyTypeName(this Type type)
    {
        if (!type.IsGenericType)
            return type.FullName!;

        string genericTypeName = type.GetGenericTypeDefinition().FullName;
        genericTypeName = genericTypeName?.Substring(0, genericTypeName.IndexOf('`'));

        string genericArgs = String.Join(",", type.GetGenericArguments().Select(GetFriendlyTypeName));

        return $"{genericTypeName}<{genericArgs}>";
    }
}
