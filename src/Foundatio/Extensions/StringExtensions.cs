using System;
using System.Text.RegularExpressions;

namespace Foundatio.Extensions {
    public static class StringExtensions {
        private static readonly Regex _whitespace = new Regex(@"\s");
        public static string RemoveWhiteSpace(this string s) {
            return _whitespace.Replace(s, String.Empty);
        }

        public static bool IsJson(this string value) {
            return value.GetJsonType() != JsonType.None;
        }

        public static JsonType GetJsonType(this string value) {
            if (String.IsNullOrEmpty(value))
                return JsonType.None;

            for (int i = 0; i < value.Length; i++) {
                if (Char.IsWhiteSpace(value[i]))
                    continue;

                if (value[i] == '{')
                    return JsonType.Object;

                if (value[i] == '[')
                    return JsonType.Array;

                break;
            }

            return JsonType.None;
        }
    }
    
    public enum JsonType : byte {
        None,
        Object,
        Array
    }
}
