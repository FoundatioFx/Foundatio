#define NETCORE
using System;
using System.Linq;
using System.Reflection;

namespace Foundatio.Force.DeepCloner.Helpers
{
	internal static class ReflectionHelper
	{
		public static bool IsEnum(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().IsEnum;
#else
			return t.IsEnum;
#endif
		}

		public static bool IsValueType(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().IsValueType;
#else
			return t.IsValueType;
#endif
		}

		public static bool IsClass(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().IsClass;
#else
			return t.IsClass;
#endif
		}

		public static Type BaseType(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().BaseType;
#else
			return t.BaseType;
#endif
		}

		public static FieldInfo[] GetAllFields(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().DeclaredFields.Where(x => !x.IsStatic).ToArray();
#else
			return t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
#endif
		}
		
		public static PropertyInfo[] GetPublicProperties(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().DeclaredProperties.ToArray();
#else
			return t.GetProperties(BindingFlags.Instance | BindingFlags.Public);
#endif
		}

		public static FieldInfo[] GetDeclaredFields(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().DeclaredFields.Where(x => !x.IsStatic).ToArray();
#else
			return t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
#endif
		}

		public static ConstructorInfo[] GetPrivateConstructors(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().DeclaredConstructors.ToArray();
#else
			return t.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
#endif
		}

		public static ConstructorInfo[] GetPublicConstructors(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().DeclaredConstructors.ToArray();
#else
			return t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
#endif
		}

		public static MethodInfo GetPrivateMethod(this Type t, string methodName)
		{
#if NETCORE
			return t.GetTypeInfo().GetDeclaredMethod(methodName);
#else
			return t.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
#endif
		}

		public static MethodInfo GetMethod(this Type t, string methodName)
		{
#if NETCORE
			return t.GetTypeInfo().GetDeclaredMethod(methodName);
#else
			return t.GetMethod(methodName);
#endif
		}

		public static MethodInfo GetPrivateStaticMethod(this Type t, string methodName)
		{
#if NETCORE
			return t.GetTypeInfo().GetDeclaredMethod(methodName);
#else
			return t.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
#endif
		}

		public static FieldInfo GetPrivateField(this Type t, string fieldName)
		{
#if NETCORE
			return t.GetTypeInfo().GetDeclaredField(fieldName);
#else
			return t.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
#endif
		}

		public static FieldInfo GetPrivateStaticField(this Type t, string fieldName)
		{
#if NETCORE
			return t.GetTypeInfo().GetDeclaredField(fieldName);
#else
			return t.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
#endif
		}

#if NETCORE
		public static bool IsSubclassOfTypeByName(this Type t, string typeName)
		{
			while (t != null)
			{
				if (t.Name == typeName)
					return true;
				t = t.BaseType();
			}

			return false;
		}
#endif

#if NETCORE
		public static bool IsAssignableFrom(this Type from, Type to)
		{
			return from.GetTypeInfo().IsAssignableFrom(to.GetTypeInfo());
		}

		public static bool IsInstanceOfType(this Type from, object to)
		{
			return from.IsAssignableFrom(to.GetType());
		}
#endif
		
		public static Type[] GenericArguments(this Type t)
		{
#if NETCORE
			return t.GetTypeInfo().GenericTypeArguments;
#else
			return t.GetGenericArguments();
#endif
		}
	}
}