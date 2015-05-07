using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Foundatio.ServiceProviders {
    public static class ServiceProvider {
        private static IServiceProvider _serviceProvider;
        private static readonly Lazy<IServiceProvider> _defaultServiceProvider = new Lazy<IServiceProvider>(() => FindServiceProvider()); 

        public static IServiceProvider Current
        {
            get { return _serviceProvider ?? _defaultServiceProvider.Value; }
            set { _serviceProvider = value; }
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
                a.GetTypes().Where(t => typeof(IBootstrappedServiceProvider).IsAssignableFrom(t)));

            foreach (var serviceProviderType in serviceProviderTypes) {
                var bootstrapper = Activator.CreateInstance(serviceProviderType) as IServiceProvider;
                if (bootstrapper != null)
                    return bootstrapper;
            }

            serviceProviderTypes = assemblies.SelectMany(a => a.GetTypes()
                .Where(t => typeof(IServiceProvider).IsAssignableFrom(t)));

            foreach (var serviceProviderType in serviceProviderTypes) {
                var bootstrapper = Activator.CreateInstance(serviceProviderType) as IServiceProvider;
                if (bootstrapper != null)
                    return bootstrapper;
            }

            return new ActivatorServiceProvider();
        }

        internal static void SetServiceProvider(Type serviceProviderType = null, Assembly[] assembliesToSearch = null) {
            if (serviceProviderType == null) {
                Current = FindServiceProvider(assembliesToSearch);
                return;
            }

            if (!typeof(IServiceProvider).IsAssignableFrom(serviceProviderType)) {
                var assemblies = new List<Assembly>();
                if (assembliesToSearch != null)
                    assemblies.AddRange(assembliesToSearch);

                assemblies.Add(serviceProviderType.Assembly);

                Current = FindServiceProvider(assemblies.ToArray());
                return;
            }

            var bootstrapper = Activator.CreateInstance(serviceProviderType) as IServiceProvider;
            if (bootstrapper == null)
                return;

            Current = bootstrapper;
        }
    }
}
