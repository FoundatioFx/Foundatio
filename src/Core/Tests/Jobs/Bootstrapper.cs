using System;
using Foundatio.Dependency;

namespace Foundatio.Tests.Jobs {
    public class Bootstrapper : IBootstrapper {
        public IDependencyResolver GetResolver() {
            return new DelegateBasedDependencyResolver(GetService, GetServices);
        }

        private object GetService(Type type) {
            if (type == typeof (WithDependencyJob))
                return new WithDependencyJob(new MyDependency { MyProperty = 5 });

            return Activator.CreateInstance(type);
        }

        private object[] GetServices(Type type) {
            return null;
        }
    }
}
