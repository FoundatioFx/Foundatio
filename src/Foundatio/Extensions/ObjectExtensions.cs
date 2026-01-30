using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Utility;

public static class ObjectExtensions
{
    [return: NotNullIfNotNull(nameof(original))]
    public static T? DeepClone<T>(this T? original)
    {
        return Foundatio.FastCloner.Code.FastClonerGenerator.CloneObject(original);
    }
}
