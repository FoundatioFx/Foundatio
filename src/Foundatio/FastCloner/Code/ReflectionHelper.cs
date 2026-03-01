#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
namespace Foundatio.FastCloner.Code;

internal static class ReflectionHelper
{
    extension(Type t)
    {
        public bool IsEnum() => t.IsEnum;
        public bool IsValueType() => t.IsValueType;
        public bool IsClass() => t.IsClass;
        public Type? BaseType() => t.BaseType;
        public FieldInfo[] GetAllFields() => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        public PropertyInfo[] GetPublicProperties() => t.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        public FieldInfo[] GetDeclaredFields() => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
        public ConstructorInfo[] GetPrivateConstructors() => t.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
        public ConstructorInfo[] GetPublicConstructors() => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        public MethodInfo? GetPrivateMethod(string methodName) => t.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        public MethodInfo? GetMethod(string methodName) => t.GetMethod(methodName);
        public MethodInfo? GetPrivateStaticMethod(string methodName) => t.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        public FieldInfo? GetPrivateField(string fieldName) => t.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        public FieldInfo? GetPrivateStaticField(string fieldName) => t.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);

        public bool IsSubclassOfTypeByName(string typeName)
        {
            while (t != null)
            {
                if (t.Name == typeName)
                    return true;
                t = t.BaseType();
            }

            return false;
        }

        public Type[] GenericArguments() => t.GetGenericArguments();
    }
}
