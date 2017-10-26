using System;
using System.Collections.Concurrent;

namespace Foundatio.Force.DeepCloner.Helpers
{
	internal static class DeepClonerCache
	{
		private static readonly ConcurrentDictionary<Type, object> _typeCache = new ConcurrentDictionary<Type, object>();

		private static readonly ConcurrentDictionary<Type, object> _typeCacheDeepTo = new ConcurrentDictionary<Type, object>();

		private static readonly ConcurrentDictionary<Type, object> _typeCacheShallowTo = new ConcurrentDictionary<Type, object>();

		private static readonly ConcurrentDictionary<Type, object> _structAsObjectCache = new ConcurrentDictionary<Type, object>();

		private static readonly ConcurrentDictionary<Tuple<Type, Type>, object> _typeConvertCache = new ConcurrentDictionary<Tuple<Type, Type>, object>();

		public static object GetOrAddClass<T>(Type type, Func<Type, T> adder)
		{
			// return _typeCache.GetOrAdd(type, x => adder(x));
			
			// this implementation is slightly faster than getoradd
			object value;
			if (_typeCache.TryGetValue(type, out value)) return value;
			value = adder(type);
			_typeCache.TryAdd(type, value);
			return value;
		}

		public static object GetOrAddDeepClassTo<T>(Type type, Func<Type, T> adder)
		{
			object value;
			if (_typeCacheDeepTo.TryGetValue(type, out value)) return value;
			value = adder(type);
			_typeCacheDeepTo.TryAdd(type, value);
			return value;
		}

		public static object GetOrAddShallowClassTo<T>(Type type, Func<Type, T> adder)
		{
			object value;
			if (_typeCacheShallowTo.TryGetValue(type, out value)) return value;
			value = adder(type);
			_typeCacheShallowTo.TryAdd(type, value);
			return value;
		}

		public static object GetOrAddStructAsObject<T>(Type type, Func<Type, T> adder)
		{
			// return _typeCache.GetOrAdd(type, x => adder(x));

			// this implementation is slightly faster than getoradd
			object value;
			if (_structAsObjectCache.TryGetValue(type, out value)) return value;
			value = adder(type);
			_structAsObjectCache.TryAdd(type, value);
			return value;
		}

		public static T GetOrAddConvertor<T>(Type from, Type to, Func<Type, Type, T> adder)
		{
			return (T)_typeConvertCache.GetOrAdd(new Tuple<Type, Type>(from, to), (tuple) => adder(tuple.Item1, tuple.Item2));
		}

		/// <summary>
		/// This method can be used when we switch between safe / unsafe variants (for testing)
		/// </summary>
		public static void ClearCache()
		{
			_typeCache.Clear();
			_typeCacheDeepTo.Clear();
			_typeCacheShallowTo.Clear();
			_structAsObjectCache.Clear();
			_typeConvertCache.Clear();
		}
	}
}
