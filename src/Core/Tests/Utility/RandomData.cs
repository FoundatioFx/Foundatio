﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Foundatio.Tests.Utility {
    internal static class RandomData {
        private static readonly Random _random;

        static RandomData() {
            _random = new Random(Environment.TickCount);
        }

        public static Random Instance => _random;

        public static int GetInt(int min, int max) {
            if (min == max)
                return min;

            if (min >= max)
                throw new Exception("Min value must be less than max value.");

            return Instance.Next(min, max + 1);
        }

        public static string GetVersion(string min, string max) {
            if (String.IsNullOrEmpty(min))
                min = "0.0.0.0";
            if (String.IsNullOrEmpty(max))
                min = "25.100.9999.9999";

            Version minVersion, maxVersion;
            if (!Version.TryParse(min, out minVersion))
                minVersion = new Version(0, 0, 0, 0);
            if (!Version.TryParse(max, out maxVersion))
                maxVersion = new Version(25, 100, 9999, 9999);

            minVersion = new Version(
                minVersion.Major != -1 ? minVersion.Major : 0,
                minVersion.Minor != -1 ? minVersion.Minor : 0,
                minVersion.Build != -1 ? minVersion.Build : 0,
                minVersion.Revision != -1 ? minVersion.Revision : 0);

            maxVersion = new Version(
                maxVersion.Major != -1 ? maxVersion.Major : 0,
                maxVersion.Minor != -1 ? maxVersion.Minor : 0,
                maxVersion.Build != -1 ? maxVersion.Build : 0,
                maxVersion.Revision != -1 ? maxVersion.Revision : 0);

            var major = GetInt(minVersion.Major, maxVersion.Major);
            var minor = GetInt(minVersion.Minor, major == maxVersion.Major ? maxVersion.Minor : 100);
            var build = GetInt(minVersion.Build, minor == maxVersion.Minor ? maxVersion.Build : 9999);
            var revision = GetInt(minVersion.Revision, build == maxVersion.Build ? maxVersion.Revision : 9999);

            return new Version(major, minor, build, revision).ToString();
        }

        public static int GetInt() {
            return GetInt(Int32.MinValue, Int32.MaxValue);
        }

        public static long GetLong(long min, long max) {
            if (min == max)
                return min;

            if (min >= max)
                throw new Exception("Min value must be less than max value.");

            var buf = new byte[8];
            Instance.NextBytes(buf);
            long longRand = BitConverter.ToInt64(buf, 0);

            return (Math.Abs(longRand % (max - min)) + min);
        }

        public static long GetLong() {
            return GetLong(Int64.MinValue, Int64.MaxValue);
        }

        public static string GetCoordinate() {
            return GetDouble(-90.0, 90.0) + "," + GetDouble(-180.0, 180.0);
        }

        public static DateTime GetDateTime(DateTime? start = null, DateTime? end = null) {
            if (start.HasValue && end.HasValue && start.Value == end.Value)
                return start.Value;

            if (start.HasValue && end.HasValue && start.Value >= end.Value)
                throw new Exception("Start date must be less than end date.");

            start = start ?? DateTime.MinValue;
            end = end ?? DateTime.MaxValue;

            TimeSpan timeSpan = end.Value - start.Value;
            var newSpan = new TimeSpan(GetLong(0, timeSpan.Ticks));

            return start.Value + newSpan;
        }

        public static DateTimeOffset GetDateTimeOffset(DateTimeOffset? start = null, DateTimeOffset? end = null) {
            if (start.HasValue && end.HasValue && start.Value >= end.Value)
                throw new Exception("Start date must be less than end date.");

            start = start ?? DateTimeOffset.MinValue;
            end = end ?? DateTimeOffset.MaxValue;

            TimeSpan timeSpan = end.Value - start.Value;
            var newSpan = new TimeSpan(GetLong(0, timeSpan.Ticks));

            return start.Value + newSpan;
        }

        public static TimeSpan GetTimeSpan(TimeSpan? min = null, TimeSpan? max = null) {
            if (min.HasValue && max.HasValue && min.Value == max.Value)
                return min.Value;

            if (min.HasValue && max.HasValue && min.Value >= max.Value)
                throw new Exception("Min span must be less than max span.");

            min = min ?? TimeSpan.Zero;
            max = max ?? TimeSpan.MaxValue;

            return min.Value + new TimeSpan((long)(new TimeSpan(max.Value.Ticks - min.Value.Ticks).Ticks * Instance.NextDouble()));
        }

        public static bool GetBool(int chance = 50) {
            chance = Math.Min(chance, 100);
            chance = Math.Max(chance, 0);
            double c = 1 - (chance / 100.0);
            return Instance.NextDouble() > c;
        }

        public static double GetDouble(double? min = null, double? max = null) {
            if (min.HasValue && max.HasValue && min.Value == max.Value)
                return min.Value;

            if (min.HasValue && max.HasValue && min.Value >= max.Value)
                throw new Exception("Min value must be less than max value.");

            min = min ?? Double.MinValue;
            max = max ?? Double.MaxValue;

            return Instance.NextDouble() * (max.Value - min.Value) + min.Value;
        }

        public static decimal GetDecimal() {
            byte scale = (byte)Instance.Next(29);
            bool sign = Instance.Next(2) == 1;
            return new decimal(GetInt(),
                               GetInt(),
                               GetInt(),
                               sign,
                               scale);
        }

        public static T GetEnum<T>() {
            if (!typeof(T).IsEnum)
                throw new ArgumentException("T must be an enum type.");

            Array values = Enum.GetValues(typeof(T));
            return (T)values.GetValue(GetInt(0, values.Length));
        }

        public static string GetIp4Address() {
            return String.Concat(GetInt(0, 255), ".", GetInt(0, 255), ".", GetInt(0, 255), ".", GetInt(0, 255));
        }

        private const string DEFAULT_RANDOM_CHARS = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        public static string GetString(int minLength = 5, int maxLength = 20, string allowedChars = DEFAULT_RANDOM_CHARS) {
            int length = minLength != maxLength ? GetInt(minLength, maxLength) : minLength;

            const int byteSize = 0x100;
            var allowedCharSet = new HashSet<char>(allowedChars).ToArray();
            if (byteSize < allowedCharSet.Length)
                throw new ArgumentException(String.Format("allowedChars may contain no more than {0} characters.", byteSize));

            using (var rng = new RNGCryptoServiceProvider()) {
                var result = new StringBuilder();
                var buf = new byte[128];

                while (result.Length < length) {
                    rng.GetBytes(buf);
                    for (var i = 0; i < buf.Length && result.Length < length; ++i) {
                        var outOfRangeStart = byteSize - (byteSize % allowedCharSet.Length);
                        if (outOfRangeStart <= buf[i])
                            continue;
                        result.Append(allowedCharSet[buf[i] % allowedCharSet.Length]);
                    }
                }

                return result.ToString();
            }
        }

        private const string DEFAULT_ALPHA_CHARS = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
        public static string GetAlphaString(int minLength = 5, int maxLength = 20) {
            return GetString(minLength, maxLength, DEFAULT_ALPHA_CHARS);
        }

        private const string DEFAULT_ALPHANUMERIC_CHARS = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        public static string GetAlphaNumericString(int minLength = 5, int maxLength = 20) {
            return GetString(minLength, maxLength, DEFAULT_ALPHANUMERIC_CHARS);
        }

        public static string GetTitleWords(int minWords = 2, int maxWords = 10) {
            return GetWords(minWords, maxWords, titleCaseAllWords: true);
        }

        public static string GetWord(bool titleCase = true) {
            return titleCase ? UpperCaseFirstCharacter(_words[GetInt(0, _words.Length - 1)]) : _words[GetInt(0, _words.Length - 1)];
        }

        public static string GetWords(int minWords = 2, int maxWords = 10, bool titleCaseFirstWord = true, bool titleCaseAllWords = true) {
            if (minWords < 2)
                throw new ArgumentException("minWords must 2 or more.", "minWords");
            if (maxWords < 2)
                throw new ArgumentException("maxWords must 2 or more.", "maxWords");

            var builder = new StringBuilder();
            int numberOfWords = GetInt(minWords, maxWords);
            for (int i = 1; i < numberOfWords; i++)
                builder.Append(' ').Append(GetWord(titleCaseAllWords || (i == 0 && titleCaseFirstWord)));

            return builder.ToString().Trim();
        }

        public static string GetSentence(int minWords = 5, int maxWords = 25) {
            if (minWords < 3)
                throw new ArgumentException("minWords must 3 or more.", "minWords");
            if (maxWords < 3)
                throw new ArgumentException("maxWords must 3 or more.", "maxWords");

            var builder = new StringBuilder();
            builder.Append(UpperCaseFirstCharacter(_words[GetInt(0, _words.Length)]));
            int numberOfWords = GetInt(minWords, maxWords);
            for (int i = 1; i < numberOfWords; i++)
                builder.Append(' ').Append(_words[GetInt(0, _words.Length)]);

            builder.Append('.');
            return builder.ToString();
        }

        private static string UpperCaseFirstCharacter(string input) {
            if (String.IsNullOrEmpty(input))
                return null;

            char[] inputChars = input.ToCharArray();
            for (int i = 0; i < inputChars.Length; ++i) {
                if (inputChars[i] != ' ' && inputChars[i] != '\t') {
                    inputChars[i] = Char.ToUpper(inputChars[i]);
                    break;
                }
            }

            return new String(inputChars);
        }

        public static string GetParagraphs(int count = 3, int minSentences = 3, int maxSentences = 25, int minSentenceWords = 5, int maxSentenceWords = 25, bool html = false) {
            if (count < 1)
                throw new ArgumentException("Count must be 1 or more.", "count");
            if (minSentences < 1)
                throw new ArgumentException("minSentences must be 1 or more.", "minSentences");

            var builder = new StringBuilder();
            if (html)
                builder.Append("<p>");

            builder.Append("Lorem ipsum dolor sit amet. ");
            int sentenceCount = GetInt(minSentences, maxSentences) - 1;

            for (int i = 0; i < sentenceCount; i++)
                builder.Append(GetSentence(minSentenceWords, maxSentenceWords)).Append(" ");

            if (html)
                builder.Append("</p>");

            for (int i = 1; i < count; i++) {
                if (html)
                    builder.Append("<p>");
                for (int x = 0; x < sentenceCount; x++)
                    builder.Append(GetSentence(minSentenceWords, maxSentenceWords)).Append(" ");

                if (html)
                    builder.Append("</p>");
                else
                    builder.Append(Environment.NewLine).Append(Environment.NewLine);
            }

            return builder.ToString();
        }

        private static string[] _words = { "consetetur", "sadipscing", "elitr", "sed", "diam", "nonumy", "eirmod",
         "tempor", "invidunt", "ut", "labore", "et", "dolore", "magna", "aliquyam", "erat", "sed", "diam", "voluptua",
         "at", "vero", "eos", "et", "accusam", "et", "justo", "duo", "dolores", "et", "ea", "rebum", "stet", "clita",
         "kasd", "gubergren", "no", "sea", "takimata", "sanctus", "est", "lorem", "ipsum", "dolor", "sit", "amet",
         "lorem", "ipsum", "dolor", "sit", "amet", "consetetur", "sadipscing", "elitr", "sed", "diam", "nonumy", "eirmod",
         "tempor", "invidunt", "ut", "labore", "et", "dolore", "magna", "aliquyam", "erat", "sed", "diam", "voluptua",
         "at", "vero", "eos", "et", "accusam", "et", "justo", "duo", "dolores", "et", "ea", "rebum", "stet", "clita",
         "kasd", "gubergren", "no", "sea", "takimata", "sanctus", "est", "lorem", "ipsum", "dolor", "sit", "amet",
         "lorem", "ipsum", "dolor", "sit", "amet", "consetetur", "sadipscing", "elitr", "sed", "diam", "nonumy", "eirmod",
         "tempor", "invidunt", "ut", "labore", "et", "dolore", "magna", "aliquyam", "erat", "sed", "diam", "voluptua",
         "at", "vero", "eos", "et", "accusam", "et", "justo", "duo", "dolores", "et", "ea", "rebum", "stet", "clita",
         "kasd", "gubergren", "no", "sea", "takimata", "sanctus", "est", "lorem", "ipsum", "dolor", "sit", "amet", "duis",
         "autem", "vel", "eum", "iriure", "dolor", "in", "hendrerit", "in", "vulputate", "velit", "esse", "molestie",
         "consequat", "vel", "illum", "dolore", "eu", "feugiat", "nulla", "facilisis", "at", "vero", "eros", "et",
         "accumsan", "et", "iusto", "odio", "dignissim", "qui", "blandit", "praesent", "luptatum", "zzril", "delenit",
         "augue", "duis", "dolore", "te", "feugait", "nulla", "facilisi", "lorem", "ipsum", "dolor", "sit", "amet",
         "consectetuer", "adipiscing", "elit", "sed", "diam", "nonummy", "nibh", "euismod", "tincidunt", "ut", "laoreet",
         "dolore", "magna", "aliquam", "erat", "volutpat", "ut", "wisi", "enim", "ad", "minim", "veniam", "quis",
         "nostrud", "exerci", "tation", "ullamcorper", "suscipit", "lobortis", "nisl", "ut", "aliquip", "ex", "ea",
         "commodo", "consequat", "duis", "autem", "vel", "eum", "iriure", "dolor", "in", "hendrerit", "in", "vulputate",
         "velit", "esse", "molestie", "consequat", "vel", "illum", "dolore", "eu", "feugiat", "nulla", "facilisis", "at",
         "vero", "eros", "et", "accumsan", "et", "iusto", "odio", "dignissim", "qui", "blandit", "praesent", "luptatum",
         "zzril", "delenit", "augue", "duis", "dolore", "te", "feugait", "nulla", "facilisi", "nam", "liber", "tempor",
         "cum", "soluta", "nobis", "eleifend", "option", "congue", "nihil", "imperdiet", "doming", "id", "quod", "mazim",
         "placerat", "facer", "possim", "assum", "lorem", "ipsum", "dolor", "sit", "amet", "consectetuer", "adipiscing",
         "elit", "sed", "diam", "nonummy", "nibh", "euismod", "tincidunt", "ut", "laoreet", "dolore", "magna", "aliquam",
         "erat", "volutpat", "ut", "wisi", "enim", "ad", "minim", "veniam", "quis", "nostrud", "exerci", "tation",
         "ullamcorper", "suscipit", "lobortis", "nisl", "ut", "aliquip", "ex", "ea", "commodo", "consequat", "duis",
         "autem", "vel", "eum", "iriure", "dolor", "in", "hendrerit", "in", "vulputate", "velit", "esse", "molestie",
         "consequat", "vel", "illum", "dolore", "eu", "feugiat", "nulla", "facilisis", "at", "vero", "eos", "et", "accusam",
         "et", "justo", "duo", "dolores", "et", "ea", "rebum", "stet", "clita", "kasd", "gubergren", "no", "sea",
         "takimata", "sanctus", "est", "lorem", "ipsum", "dolor", "sit", "amet", "lorem", "ipsum", "dolor", "sit",
         "amet", "consetetur", "sadipscing", "elitr", "sed", "diam", "nonumy", "eirmod", "tempor", "invidunt", "ut",
         "labore", "et", "dolore", "magna", "aliquyam", "erat", "sed", "diam", "voluptua", "at", "vero", "eos", "et",
         "accusam", "et", "justo", "duo", "dolores", "et", "ea", "rebum", "stet", "clita", "kasd", "gubergren", "no",
         "sea", "takimata", "sanctus", "est", "lorem", "ipsum", "dolor", "sit", "amet", "lorem", "ipsum", "dolor", "sit",
         "amet", "consetetur", "sadipscing", "elitr", "at", "accusam", "aliquyam", "diam", "diam", "dolore", "dolores",
         "duo", "eirmod", "eos", "erat", "et", "nonumy", "sed", "tempor", "et", "et", "invidunt", "justo", "labore",
         "stet", "clita", "ea", "et", "gubergren", "kasd", "magna", "no", "rebum", "sanctus", "sea", "sed", "takimata",
         "ut", "vero", "voluptua", "est", "lorem", "ipsum", "dolor", "sit", "amet", "lorem", "ipsum", "dolor", "sit",
         "amet", "consetetur", "sadipscing", "elitr", "sed", "diam", "nonumy", "eirmod", "tempor", "invidunt", "ut",
         "labore", "et", "dolore", "magna", "aliquyam", "erat", "consetetur", "sadipscing", "elitr", "sed", "diam",
         "nonumy", "eirmod", "tempor", "invidunt", "ut", "labore", "et", "dolore", "magna", "aliquyam", "erat", "sed",
         "diam", "voluptua", "at", "vero", "eos", "et", "accusam", "et", "justo", "duo", "dolores", "et", "ea",
         "rebum", "stet", "clita", "kasd", "gubergren", "no", "sea", "takimata", "sanctus", "est", "lorem", "ipsum" };
    }

    internal static class EnumerableExtensions {
        public static T Random<T>(this IEnumerable<T> items, T defaultValue = default(T)) {
            if (items == null)
                return defaultValue;

            var list = items.ToList();
            int count = list.Count();
            if (count == 0)
                return defaultValue;

            return list.ElementAt(RandomData.Instance.Next(count));
        }
    }
}
