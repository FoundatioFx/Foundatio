using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Startup {
    public class StartupPriorityAttribute : Attribute {
        public StartupPriorityAttribute(int priority) {
            Priority = priority;
        }

        public int Priority { get; private set; }
    }
}