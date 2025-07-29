using System;
using System.Text;

namespace Foundatio.Redis.Utility;

public static class RedisPattern
{
    /// <summary>
    /// Escapes Redis glob‑pattern meta characters so they are matched literally.
    /// Meta-chars (per KEYS/SCAN docs): *, ?, [, ], and \.
    /// See https://redis.io/docs/latest/commands/keys/ and
    /// https://redis.io/docs/latest/commands/scan/ for details.
    /// </summary>
    public static string Escape(string value)
    {
        if (String.IsNullOrEmpty(value))
            return value;

        // First pass: count how many characters need escaping
        int escapeCount = 0;
        bool prevBackslash = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (prevBackslash)
            {
                prevBackslash = false;
                continue;
            }

            if (c == '\\')
            {
                prevBackslash = true;
            }
            else if (NeedsEscaping(c))
            {
                escapeCount++;
            }
        }

        // Fast path: return original string if no escaping needed
        if (escapeCount == 0)
            return value;

        // Second pass: build escaped string with exact capacity
        var result = new StringBuilder(value.Length + escapeCount);
        prevBackslash = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (prevBackslash)
            {
                result.Append(c);
                prevBackslash = false;
                continue;
            }

            if (c == '\\')
            {
                result.Append('\\');
                prevBackslash = true;
            }
            else if (NeedsEscaping(c))
            {
                result.Append('\\').Append(c);
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static bool NeedsEscaping(char c)
    {
        return c is '*' or '?' or '[' or ']';
    }
}
