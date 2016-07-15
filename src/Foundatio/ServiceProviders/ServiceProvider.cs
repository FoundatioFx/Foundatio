using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.ServiceProviders {
    public static class ServiceProvider {
        public static IServiceProvider FindAndGetServiceProvider(string relativeTypeName, ILoggerFactory loggerFactory = null) {
            if (String.IsNullOrEmpty(relativeTypeName))
                return null;

            var relativeType = TypeHelper.ResolveType(relativeTypeName, typeof(IServiceProvider), loggerFactory?.CreateLogger("Foundatio.ServiceProviders.ServiceProvider"));
            if (relativeType == null)
                return null;

            return FindAndGetServiceProvider(relativeType, loggerFactory);
        }

        public static IServiceProvider FindAndGetServiceProvider(Type relativeType, ILoggerFactory loggerFactory = null) {
            if (relativeType == null)
                return null;

            if (!typeof(IServiceProvider).IsAssignableFrom(relativeType))
                return FindAndGetServiceProvider(loggerFactory, relativeType.GetTypeInfo().Assembly);

            return GetServiceProvider(relativeType, loggerFactory);
        }

        public static IServiceProvider FindAndGetServiceProvider(params Assembly[] assembliesToSearch) {
            return FindAndGetServiceProvider(null, assembliesToSearch);
        }

        public static IServiceProvider FindAndGetServiceProvider(ILoggerFactory loggerFactory, params Assembly[] assembliesToSearch) {
            var assemblies = new List<Assembly>();
            if (assembliesToSearch != null && assembliesToSearch.Length > 0) {
                assemblies.AddRange(assembliesToSearch);
            } else {
                try {
                    var entryAssembly = Assembly.GetEntryAssembly();
                    if (entryAssembly != null)
                        assemblies.Add(entryAssembly);
                } catch { }
            }

            // try to find bootstrapped service providers first
            var serviceProviderTypes = assemblies.SelectMany(a =>
                a.GetTypes().Where(t => !t.GetTypeInfo().IsInterface && !t.GetTypeInfo().IsAbstract && typeof(IBootstrappedServiceProvider).IsAssignableFrom(t)));

            foreach (var serviceProviderType in serviceProviderTypes) {
                var serviceProvider = GetServiceProvider(serviceProviderType, loggerFactory);
                if (serviceProvider != null)
                    return serviceProvider;
            }

            // find any service providers
            serviceProviderTypes = assemblies.SelectMany(a => a.GetTypes()
                .Where(t => !t.GetTypeInfo().IsInterface && !t.GetTypeInfo().IsAbstract && typeof(IServiceProvider).IsAssignableFrom(t)));

            foreach (var serviceProviderType in serviceProviderTypes) {
                var serviceProvider = GetServiceProvider(serviceProviderType, loggerFactory);
                if (serviceProvider != null)
                    return serviceProvider;
            }

            return new ActivatorServiceProvider();
        }

        public static IServiceProvider GetServiceProvider(string serviceProviderTypeName, ILoggerFactory loggerFactory) {
            var type = Type.GetType(serviceProviderTypeName);
            if (type == null)
                return null;

            return GetServiceProvider(type, loggerFactory);
        }

        public static IServiceProvider GetServiceProvider(Type serviceProviderType, ILoggerFactory loggerFactory) {
            var serviceProvider = Activator.CreateInstance(serviceProviderType) as IServiceProvider;
            if (serviceProvider is IBootstrappedServiceProvider)
                ((IBootstrappedServiceProvider)serviceProvider).LoggerFactory = loggerFactory;

            return serviceProvider;
        }
    }
}
