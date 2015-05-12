using System;
using NLog.Fluent;

namespace Foundatio.Utility {
    public static class TypeHelper {
        public static Type ResolveType(string fullTypeName, Type expectedBase = null) {
            if (String.IsNullOrEmpty(fullTypeName))
                return null;
            
            var type = Type.GetType(fullTypeName);
            if (type == null) {
                Log.Error().Message("Unable to resolve type: \"{0}\".", fullTypeName).Write();
                return null;
            }

            if (expectedBase != null && !expectedBase.IsAssignableFrom(type)) {
                Log.Error().Message("Type \"{0}\" must be assignable to type: \"{1}\".", fullTypeName, expectedBase.FullName).Write();
                return null;
            }

            return type;
        }
    }
}
