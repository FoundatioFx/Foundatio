using System;

namespace Foundatio.Jobs {
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class JobAttribute : Attribute {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsContinuous { get; set; } = true;
        public string Interval { get; set; }
        public string InitialDelay { get; set; }
        public int IterationLimit { get; set; } = -1;
    }
}