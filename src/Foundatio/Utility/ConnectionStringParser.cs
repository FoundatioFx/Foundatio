using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Foundatio.Utility;

public static class ConnectionStringParser
{
    // borrowed from https://github.com/dotnet/corefx/blob/release/2.2/src/Common/src/System/Data/Common/DbConnectionOptions.Common.cs
    private const string ConnectionStringPattern =                  // may not contain embedded null except trailing last value
            "([\\s;]*"                                                  // leading whitespace and extra semicolons
            + "(?![\\s;])"                                              // key does not start with space or semicolon
            + "(?<key>([^=\\s\\p{Cc}]|\\s+[^=\\s\\p{Cc}]|\\s+==|==)+)"  // allow any visible character for keyname except '=' which must quoted as '=='
            + "\\s*=(?!=)\\s*"                                          // the equal sign divides the key and value parts
            + "(?<value>"
            + "(\"([^\"\u0000]|\"\")*\")"                               // double quoted string, " must be quoted as ""
            + "|"
            + "('([^'\u0000]|'')*')"                                    // single quoted string, ' must be quoted as ''
            + "|"
            + "((?![\"'\\s])"                                           // unquoted value must not start with " or ' or space, would also like = but too late to change
            + "([^;\\s\\p{Cc}]|\\s+[^;\\s\\p{Cc}])*"                    // control characters must be quoted
            + "(?<![\"']))"                                             // unquoted value must not stop with " or '
            + ")(\\s*)(;|[\u0000\\s]*$)"                                // whitespace after value up to semicolon or end-of-line
            + ")*"                                                      // repeat the key-value pair
            + "[\\s;]*[\u0000\\s]*"                                     // trailing whitespace/semicolons (DataSourceLocator), embedded nulls are allowed only in the end
        ;

    private static readonly Regex _connectionStringRegex = new(ConnectionStringPattern, RegexOptions.ExplicitCapture | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static Dictionary<string, string> Parse(string connectionString, IDictionary<string, string> synonyms, string defaultKey = null)
    {
        var parseTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        const int keyIndex = 1, valueIndex = 2;
        Debug.Assert(keyIndex == _connectionStringRegex.GroupNumberFromName("key"), "wrong key index");
        Debug.Assert(valueIndex == _connectionStringRegex.GroupNumberFromName("value"), "wrong value index");

        if (null == connectionString)
            return parseTable;

        var match = _connectionStringRegex.Match(connectionString);
        if (!match.Success || (match.Length != connectionString.Length))
        {
            if (defaultKey != null)
            {
                parseTable[defaultKey] = connectionString;
                return parseTable;
            }

            throw new ArgumentException($"Format of the initialization string does not conform to specification starting at index {match.Length}");
        }

        int indexValue = 0;
        var keyValues = match.Groups[valueIndex].Captures;
        foreach (Capture keyPair in match.Groups[keyIndex].Captures)
        {
            string keyName = keyPair.Value.Replace("==", "=");
            string keyValue = keyValues[indexValue++].Value;
            if (0 < keyValue.Length)
            {
                switch (keyValue[0])
                {
                    case '\"':
                        keyValue = keyValue.Substring(1, keyValue.Length - 2).Replace("\"\"", "\"");
                        break;
                    case '\'':
                        keyValue = keyValue.Substring(1, keyValue.Length - 2).Replace("\'\'", "\'");
                        break;
                }
            }
            else
            {
                keyValue = null;
            }

            string realKeyName = synonyms != null ? (synonyms.TryGetValue(keyName, out string synonym) ? synonym : null) : keyName;

            if (!IsKeyNameValid(realKeyName))
                throw new ArgumentException($"Keyword not supported: '{keyName}'");

            if (!parseTable.ContainsKey(realKeyName))
                parseTable[realKeyName] = keyValue; // last key-value pair wins (or first)
        }

        return parseTable;
    }

    private static bool IsKeyNameValid(string keyName)
    {
        if (String.IsNullOrEmpty(keyName))
            return false;

        return keyName[0] != ';' && !Char.IsWhiteSpace(keyName[0]) && keyName.IndexOf('\u0000') == -1;
    }

    public static Dictionary<string, string> ParseConnectionString(this string connectionString, IDictionary<string, string> synonyms = null, string defaultKey = null)
    {
        return Parse(connectionString, synonyms, defaultKey);
    }

    public static string BuildConnectionString(this IDictionary<string, string> options, IEnumerable<string> excludedKeys = null)
    {
        if (options == null || options.Count == 0)
            return null;

        var excludes = new HashSet<string>(excludedKeys ?? new string[] { }, StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var option in options)
        {
            if (excludes.Contains(option.Key))
                continue;

            if (option.Value != null && option.Value.Contains("\""))
                builder.Append($"{option.Key}=\"{option.Value.Replace("\"", "\"\"")}\";");
            else
                builder.Append($"{option.Key}={option.Value};");
        }

        return builder.ToString().TrimEnd(';');
    }
}
