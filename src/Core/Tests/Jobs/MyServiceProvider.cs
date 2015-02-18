using System;
using Foundatio.ServiceProvider;

namespace Foundatio.Tests.Jobs {
    public class MyServiceProvider : IServiceProvider {
       public object GetService(Type type) {
            if (type == typeof (WithDependencyJob))
                return new WithDependencyJob(new MyDependency { MyProperty = 5 });

            return Activator.CreateInstance(type);
        }
    }

    public class MyBootstrappedServiceProvider : BootstrappedServiceProviderBase {
        public override IServiceProvider Bootstrap() {
            // create container, do registrations and return the service provider instance.
            return new MyServiceProvider();
        }
    }
}
