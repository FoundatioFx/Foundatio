using System;
using System.IO;
using System.Text;

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

    public static string ToSpacedWords(this string text, bool preserveAcronyms = true)
    {
        if (String.IsNullOrWhiteSpace(text))
            return String.Empty;

        var sb = new StringBuilder(text.Length * 2);
        sb.Append(text[0]);

        for (int i = 1; i < text.Length; i++)
        {
            if (Char.IsUpper(text[i]))
                if ((text[i - 1] != ' ' && !Char.IsUpper(text[i - 1])) ||
                    (preserveAcronyms && Char.IsUpper(text[i - 1]) &&
                     i < text.Length - 1 && !Char.IsUpper(text[i + 1])))
                    sb.Append(' ');

            sb.Append(text[i]);
        }

        return sb.ToString();
    }
}
