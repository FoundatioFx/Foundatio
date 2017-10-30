using System;

namespace Foundatio.Force.DeepCloner.Helpers
{
	internal static class ShallowClonerGenerator
	{
		public static T CloneObject<T>(T obj)
		{
			// this is faster than typeof(T).IsValueType
			if (obj is ValueType)
				if (typeof(T) == obj.GetType()) return obj;

			return (T)ShallowObjectCloner.CloneObject(obj);
		}
	}
}
