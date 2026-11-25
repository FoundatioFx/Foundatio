using System.Globalization;

namespace Foundatio.Chaos;

/// <summary>
/// Settings from <c>--name value</c> arguments, falling back to <c>CHAOS_NAME</c> environment variables
/// (upper case, dashes as underscores), then to the default.
/// </summary>
public sealed class ChaosSettings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public ChaosSettings(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;

            string key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                _values[key] = args[++i];
            else
                _values[key] = "true";
        }
    }

    public string? Get(string name)
    {
        if (_values.TryGetValue(name, out string? value))
            return value;

        return Environment.GetEnvironmentVariable("CHAOS_" + name.Replace('-', '_').ToUpperInvariant());
    }

    public string Get(string name, string defaultValue) => Get(name) is { Length: > 0 } value ? value : defaultValue;

    public string Require(string name) => Get(name) is { Length: > 0 } value
        ? value
        : throw new ArgumentException($"--{name} (or CHAOS_{name.Replace('-', '_').ToUpperInvariant()}) is required");

    public int GetInt(string name, int defaultValue) => Get(name) is { Length: > 0 } value ? Int32.Parse(value, CultureInfo.InvariantCulture) : defaultValue;

    public long GetLong(string name, long defaultValue) => Get(name) is { Length: > 0 } value ? Int64.Parse(value, CultureInfo.InvariantCulture) : defaultValue;

    public double GetDouble(string name, double defaultValue) => Get(name) is { Length: > 0 } value ? Double.Parse(value, CultureInfo.InvariantCulture) : defaultValue;

    public bool GetBool(string name, bool defaultValue = false) => Get(name) is { Length: > 0 } value ? Boolean.Parse(value) : defaultValue;

    public string Transport => Get("transport", "memory");

    public string Prefix => Get("prefix", "chaos");

    public string RunDirectory => Get("run-dir", Path.Combine(Environment.CurrentDirectory, "chaos-run"));

    public string Name => Get("name", "proc");

    public bool Jobs => GetBool("jobs", Transport == "redis");
}
