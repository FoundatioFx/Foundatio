using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Foundatio.Logging;
using Microsoft.Extensions.Logging;

namespace Foundatio.Utility {
    public static class TypeHelper {
        public static Type ResolveType(string fullTypeName, Type expectedBase = null, ILogger logger = null) {
            if (String.IsNullOrEmpty(fullTypeName))
                return null;
            
            var type = Type.GetType(fullTypeName);
            if (type == null) {
                logger.Error().Message("Unable to resolve type: \"{0}\".", fullTypeName).Write();
                return null;
            }

            if (expectedBase != null && !expectedBase.IsAssignableFrom(type)) {
                logger.Error().Message("Type \"{0}\" must be assignable to type: \"{1}\".", fullTypeName, expectedBase.FullName).Write();
                return null;
            }

            return type;
        }

        public static IEnumerable<Type> GetDerivedTypes<TAction>(IEnumerable<Assembly> assemblies = null) {
            if (assemblies == null)
                assemblies = AppDomain.CurrentDomain.GetAssemblies();

            var types = new List<Type>();
            foreach (var assembly in assemblies) {
                try {
                    types.AddRange(from type in assembly.GetTypes() where type.IsClass && !type.IsNotPublic && !type.IsAbstract && typeof(TAction).IsAssignableFrom(type) select type);
                } catch (ReflectionTypeLoadException ex) {
                    string loaderMessages = String.Join(", ", ex.LoaderExceptions.ToList().Select(le => le.Message));
                    Trace.TraceInformation("Unable to search types from assembly \"{0}\" for plugins of type \"{1}\": {2}", assembly.FullName, typeof(TAction).Name, loaderMessages);
                }
            }

            return types;
        }
    }
}
