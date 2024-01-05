using System;
using System.IO;

namespace Foundatio.Extensions;

internal static class StringExtensions
{
    public static string NormalizePath(this string path)
    {
        if (String.IsNullOrEmpty(path))
            return path;

        if (Path.DirectorySeparatorChar == '\\')
            path = path.Replace('/', Path.DirectorySeparatorChar);
        else if (Path.DirectorySeparatorChar == '/')
            path = path.Replace('\\', Path.DirectorySeparatorChar);

        return path;
    }
}
