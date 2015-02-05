using System;
using System.Text.RegularExpressions;

namespace Foundatio.Redis.Extensions {
    public static class StringExtensions {
        private static readonly Regex _whitespace = new Regex(@"\s");
        public static string RemoveWhiteSpace(this string s) {
            return _whitespace.Replace(s, String.Empty);
        }
    }
}
