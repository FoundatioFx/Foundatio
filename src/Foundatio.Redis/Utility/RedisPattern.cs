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

        // Fast scan to count how many characters need escaping
        int escapeCount = 0;

        foreach (char c in value)
        {
            if (NeedsEscaping(c))
            {
                escapeCount++;
            }
        }

        // Fast path: return original string if no escaping needed
        if (escapeCount == 0)
            return value;

        // Build escaped string with exact capacity to avoid reallocations
        var result = new StringBuilder(value.Length + escapeCount);

        foreach (char c in value)
        {
            if (NeedsEscaping(c))
            {
                // Always escape meta-characters
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
        return c is '*' or '?' or '[' or ']' or '\\';
    }
}
