using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Foundatio.Utility;

namespace Foundatio.Utility {
    public static class ConnectionStringParser {
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

        private static readonly Regex _connectionStringRegex = new Regex(ConnectionStringPattern, RegexOptions.ExplicitCapture | RegexOptions.Compiled);
        private const string ConnectionStringValidKeyPattern = "^(?![;\\s])[^\\p{Cc}]+(?<!\\s)$"; // key not allowed to start with semi-colon or space or contain non-visible characters or end with space
        private static readonly Regex _connectionStringValidKeyRegex = new Regex(ConnectionStringValidKeyPattern, RegexOptions.Compiled);

        private static Dictionary<string, string> SplitConnectionString(string connectionString, IDictionary<string, string> synonyms) {
            var parsetable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Regex parser = _connectionStringRegex;

            const int KeyIndex = 1, ValueIndex = 2;
            Debug.Assert(KeyIndex == parser.GroupNumberFromName("key"), "wrong key index");
            Debug.Assert(ValueIndex == parser.GroupNumberFromName("value"), "wrong value index");

            if (null != connectionString) {
                Match match = parser.Match(connectionString);
                if (!match.Success || (match.Length != connectionString.Length))
                    throw new ArgumentException($"Format of the initialization string does not conform to specification starting at index {match.Length}.");

                int indexValue = 0;
                CaptureCollection keyvalues = match.Groups[ValueIndex].Captures;
                foreach (Capture keypair in match.Groups[KeyIndex].Captures) {
                    string keyname = keypair.Value.Replace("==", "=");
                    string keyvalue = keyvalues[indexValue++].Value;
                    if (0 < keyvalue.Length) {
                        switch (keyvalue[0]) {
                            case '\"':
                                keyvalue = keyvalue.Substring(1, keyvalue.Length - 2).Replace("\"\"", "\"");
                                break;
                            case '\'':
                                keyvalue = keyvalue.Substring(1, keyvalue.Length - 2).Replace("\'\'", "\'");
                                break;
                            default:
                                break;
                        }
                    } else {
                        keyvalue = null;
                    }

                    string synonym;
                    string realkeyname = null != synonyms ? (synonyms.TryGetValue(keyname, out synonym) ? synonym : null) : keyname;

                    if (!IsKeyNameValid(realkeyname))
                        throw new ArgumentException($"Keyword not supported: '{keyname}'.");
                    
                    if (!parsetable.ContainsKey(realkeyname))
                        parsetable[realkeyname] = keyvalue; // last key-value pair wins (or first)
                }
            }

            return parsetable;
        }

        private static bool IsKeyNameValid(string keyname) {
            if (null != keyname) {
#if DEBUG
                bool compValue = _connectionStringValidKeyRegex.IsMatch(keyname);
                Debug.Assert(((0 < keyname.Length) && (';' != keyname[0]) && !Char.IsWhiteSpace(keyname[0]) && (-1 == keyname.IndexOf('\u0000'))) == compValue, "IsValueValid mismatch with regex");
#endif
                return ((0 < keyname.Length) && (';' != keyname[0]) && !Char.IsWhiteSpace(keyname[0]) && (-1 == keyname.IndexOf('\u0000')));
            }
            return false;
        }

        public static Dictionary<string, string> ParseConnectionString(this string connectionString, IDictionary<string, string> synonyms = null) {
            return SplitConnectionString(connectionString, synonyms);
        }

        public static string BuildConnectionString(this IDictionary<string, string> options, IEnumerable<string> excludedKeys = null) {
            if (options == null || options.Count == 0)
                return null;

            var excludes = new HashSet<string>(excludedKeys ?? new string[] { }, StringComparer.OrdinalIgnoreCase);
            var builder = new StringBuilder();
            foreach (var option in options) {
                if (excludes.Contains(option.Key))
                    continue;

                if (option.Value.Contains("\""))
                    builder.Append($"{option.Key}=\"{option.Value.Replace("\"", "\"\"")}\";");
                else
                    builder.Append($"{option.Key}={option.Value};");
            }

            return builder.ToString().TrimEnd(';');
        }
    }
}