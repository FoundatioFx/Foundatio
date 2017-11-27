using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Utility {
    public static class TypeHelper {
        public static readonly Type ObjectType = typeof(object);
        public static readonly Type StringType = typeof(string);
        public static readonly Type CharType = typeof(char);
        public static readonly Type NullableCharType = typeof(char?);
        public static readonly Type DateTimeType = typeof(DateTime);
        public static readonly Type NullableDateTimeType = typeof(DateTime?);
        public static readonly Type BoolType = typeof(bool);
        public static readonly Type NullableBoolType = typeof(bool?);
        public static readonly Type ByteArrayType = typeof(byte[]);
        public static readonly Type ByteType = typeof(byte);
        public static readonly Type SByteType = typeof(sbyte);
        public static readonly Type SingleType = typeof(float);
        public static readonly Type DecimalType = typeof(decimal);
        public static readonly Type Int16Type = typeof(short);
        public static readonly Type UInt16Type = typeof(ushort);
        public static readonly Type Int32Type = typeof(int);
        public static readonly Type UInt32Type = typeof(uint);
        public static readonly Type Int64Type = typeof(long);
        public static readonly Type UInt64Type = typeof(ulong);
        public static readonly Type DoubleType = typeof(double);

        public static Type ResolveType(string fullTypeName, Type expectedBase = null, ILogger logger = null) {
            if (String.IsNullOrEmpty(fullTypeName))
                return null;

            var type = Type.GetType(fullTypeName);
            if (type == null) {
                if (logger != null && logger.IsEnabled(LogLevel.Error))
                    logger.LogError("Unable to resolve type: {TypeFullName}.", fullTypeName);
                return null;
            }

            if (expectedBase != null && !expectedBase.IsAssignableFrom(type)) {
                if (logger != null && logger.IsEnabled(LogLevel.Error))
                    logger.LogError("Type {TypeFullName} must be assignable to type: {ExpectedFullName}.", fullTypeName, expectedBase.FullName);
                return null;
            }

            return type;
        }

        private static readonly Dictionary<Type, string> _builtInTypeNames = new Dictionary<Type, string> {
            { StringType, "string" },
            { BoolType, "bool" },
            { ByteType, "byte" },
            { SByteType, "sbyte" },
            { CharType, "char" },
            { DecimalType, "decimal" },
            { DoubleType, "double" },
            { SingleType, "float" },
            { Int16Type, "short" },
            { Int32Type, "int" },
            { Int64Type, "long" },
            { ObjectType, "object" },
            { UInt16Type, "ushort" },
            { UInt32Type, "uint" },
            { UInt64Type, "ulong" }
        };

        public static string GetTypeDisplayName(Type type) {
            string fullName = null;
            if (type.GetTypeInfo().IsGenericType) {
                fullName = type.GetGenericTypeDefinition().FullName;

                // Nested types (public or private) have a '+' in their full name
                var parts = fullName.Split('+');

                // Handle nested generic types
                // Examples:
                // ConsoleApp.Program+Foo`1+Bar
                // ConsoleApp.Program+Foo`1+Bar`1
                for (int i = 0; i < parts.Length; i++) {
                    string partName = parts[i];

                    int backTickIndex = partName.IndexOf('`');
                    if (backTickIndex >= 0) {
                        // Since '.' is typically used to filter log messages in a hierarchy kind of scenario,
                        // do not include any generic type information as part of the name.
                        // Example:
                        // Microsoft.AspNetCore.Mvc -> log level set as Warning
                        // Microsoft.AspNetCore.Mvc.ModelBinding -> log level set as Verbose
                        partName = partName.Substring(0, backTickIndex);
                    }

                    parts[i] = partName;
                }

                return String.Join(".", parts);
            }

            if (_builtInTypeNames.ContainsKey(type))
                return _builtInTypeNames[type];

            fullName = type.FullName;
            if (type.IsNested)
                fullName = fullName.Replace('+', '.');

            return fullName;
        }
    }
}
