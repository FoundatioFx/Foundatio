using System.Text;

namespace Foundatio.Chaos;

/// <summary>
/// Append-only, tab-separated record of what one process did. Every line is written straight to the file, so a
/// process killed with SIGKILL loses nothing it had recorded.
/// </summary>
/// <remarks>
/// Kinds: <c>A</c> send attempted, <c>S</c> send confirmed, <c>E</c> send failed, <c>B</c> handler began,
/// <c>F</c> handler finished, <c>M</c> process metrics, <c>X</c> note.
/// </remarks>
public sealed class Ledger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly System.Threading.Lock _lock = new();

    public Ledger(string runDirectory, string name)
    {
        Directory.CreateDirectory(runDirectory);
        Path = System.IO.Path.Combine(runDirectory, $"{name}.{Environment.ProcessId}.tsv");
        var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string Path { get; }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Write(string kind, params object?[] fields)
    {
        var builder = new StringBuilder(96);
        builder.Append(Now()).Append('\t').Append(kind);
        foreach (object? field in fields)
            builder.Append('\t').Append(field?.ToString()?.Replace('\t', ' ').Replace('\n', ' ') ?? "");

        string line = builder.ToString();
        lock (_lock)
            _writer.WriteLine(line);
    }

    public void WriteMany(IEnumerable<string> lines)
    {
        lock (_lock)
        {
            foreach (string line in lines)
                _writer.Write(line);
            _writer.Flush();
        }
    }

    public static string Line(string kind, params object?[] fields)
    {
        var builder = new StringBuilder(96);
        builder.Append(Now()).Append('\t').Append(kind);
        foreach (object? field in fields)
            builder.Append('\t').Append(field?.ToString() ?? "");
        return builder.Append('\n').ToString();
    }

    public void Dispose()
    {
        lock (_lock)
            _writer.Dispose();
    }
}
