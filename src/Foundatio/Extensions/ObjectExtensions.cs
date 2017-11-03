using System;
using Foundatio.Force.DeepCloner.Helpers;

namespace Foundatio.Utility {
    public static class ObjectExtensions {
        public static T DeepClone<T>(this T original) {
            return DeepClonerGenerator.CloneObject(original);
        }
    }
}