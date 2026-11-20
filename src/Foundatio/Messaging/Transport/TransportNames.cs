using System;
using System.Linq;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>Queue, topic and subscription names contain only letters, digits, '.', '_' and '-'.</summary>
internal static class TransportNames
{
    public static string Validate(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        if (name.Any(c => !IsValid(c)))
            throw new ArgumentException($"\"{name}\" is not a valid name. Use letters, digits, '.', '_' and '-'.", parameterName);

        return name;
    }

    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
            builder.Append(IsValid(c) ? c : '-');

        return builder.ToString().Trim('-');
    }

    private static bool IsValid(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';
}
