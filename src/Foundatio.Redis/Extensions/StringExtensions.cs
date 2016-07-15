using System;
using System.Text.RegularExpressions;

namespace Foundatio.Extensions {
    internal static class StringExtensions {
        private static readonly Regex _whitespace = new Regex(@"\s");
        public static string RemoveWhiteSpace(this string s) {
            return _whitespace.Replace(s, String.Empty);
        }
    }
}