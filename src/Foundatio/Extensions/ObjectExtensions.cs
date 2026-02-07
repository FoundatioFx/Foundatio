using System.Diagnostics.CodeAnalysis;
using Foundatio.FastCloner.Code;

namespace Foundatio.Utility;

public static class ObjectExtensions
{
    [return: NotNullIfNotNull(nameof(original))]
    public static T? DeepClone<T>(this T? original)
    {
        return FastClonerGenerator.CloneObject(original);
    }
}
