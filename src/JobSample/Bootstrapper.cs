using System;
using Foundatio.ServiceProviders;
using SimpleInjector;

namespace Foundatio.JobSample.Jobs {
    public class FoundatioBootstrapper : BootstrappedServiceProviderBase {
        public override IServiceProvider Bootstrap() {
            var container = new Container();
            container.Options.AllowOverridingRegistrations = true;

            container.Register<IAmADependency, MyDependency>();

            return container;
        }
    }
}
