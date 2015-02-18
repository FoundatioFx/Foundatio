using System;
using System.Collections.Generic;

namespace Foundatio.Dependency {
    public interface IDependencyResolver {
        object GetService(Type serviceType);
        IEnumerable<object> GetServices(Type serviceType);
    }
}
