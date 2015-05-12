using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Foundatio.Utility;

namespace Foundatio.ServiceProviders {
    public static class ServiceProvider {
        private static IServiceProvider _serviceProvider;
        private static readonly Lazy<IServiceProvider> _defaultServiceProvider = new Lazy<IServiceProvider>(() => FindServiceProvider()); 

        public static IServiceProvider Current
        {
            get { return _serviceProvider ?? _defaultServiceProvider.Value; }
            set { _serviceProvider = value ?? Current; }
        }

        internal static IServiceProvider FindServiceProvider(Assembly[] assembliesToSearch = null) {
            var assemblies = new List<Assembly>();
            if (assembliesToSearch != null && assembliesToSearch.Length > 0) {
                assemblies.AddRange(assembliesToSearch);
            } else {
                try {
                    var entryAssembly = Assembly.GetEntryAssembly();
                    if (entryAssembly != null)
                        assemblies.Add(entryAssembly);
                } catch {}
            }

            var serviceProviderTypes = assemblies.SelectMany(a =>
                a.GetTypes().Where(t => !t.IsInterface && !t.IsAbstract && typeof(IBootstrappedServiceProvider).IsAssignableFrom(t)));

            foreach (var serviceProviderType in serviceProviderTypes) {
                var bootstrapper = Activator.CreateInstance(serviceProviderType) as IServiceProvider;
                if (bootstrapper != null)
                    return bootstrapper;
            }

            serviceProviderTypes = assemblies.SelectMany(a => a.GetTypes()
                .Where(t => !t.IsInterface && !t.IsAbstract && typeof(IServiceProvider).IsAssignableFrom(t)));

            foreach (var serviceProviderType in serviceProviderTypes) {
                var bootstrapper = Activator.CreateInstance(serviceProviderType) as IServiceProvider;
                if (bootstrapper != null)
                    return bootstrapper;
            }

            return new ActivatorServiceProvider();
        }

        public static void SetServiceProvider(string serviceProviderTypeName, params string[] typeNamesToSearch) {
            if (!String.IsNullOrEmpty(serviceProviderTypeName)) {
                var serviceProviderType = TypeHelper.ResolveType(serviceProviderTypeName, typeof(IServiceProvider));
                if (serviceProviderType != null) {
                    SetServiceProvider(serviceProviderType);
                    return;
                }
            }

            var assembliesToSearch = new List<Assembly>();
            var assemblyType = !String.IsNullOrEmpty(serviceProviderTypeName) ? Type.GetType(serviceProviderTypeName) : null;
            if (assemblyType != null)
                assembliesToSearch.Add(assemblyType.Assembly);

            foreach (var typeName in typeNamesToSearch) {
                var type = !String.IsNullOrEmpty(typeName) ? Type.GetType(typeName) : null;
                if (type != null)
                    assembliesToSearch.Add(type.Assembly);
            }

            SetServiceProvider(assembliesToSearch.Distinct().ToArray());
        }

        public static void SetServiceProvider(Type serviceProviderOrJobType) {
            if (serviceProviderOrJobType == null)
                return;

            if (!typeof(IServiceProvider).IsAssignableFrom(serviceProviderOrJobType)) {
                SetServiceProvider(serviceProviderOrJobType.Assembly);
                return;
            }

            var bootstrapper = Activator.CreateInstance(serviceProviderOrJobType) as IServiceProvider;
            if (bootstrapper == null)
                return;

            Current = bootstrapper;
        }
        
        public static void SetServiceProvider(params Assembly[] assembliesToSearch) {
            var result = FindServiceProvider(assembliesToSearch);
            if (result != null)
                Current = result;
        }
    }
}
