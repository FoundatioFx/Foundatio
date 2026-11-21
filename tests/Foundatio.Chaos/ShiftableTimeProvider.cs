namespace Foundatio.Chaos;

/// <summary>
/// The system clock plus an offset read from a file once a second, so a scenario can make one process's clock jump
/// forward or back while it runs (write a number of milliseconds to the file).
/// </summary>
public sealed class ShiftableTimeProvider : TimeProvider, IDisposable
{
    private readonly string _offsetFile;
    private readonly Timer _timer;
    private long _offsetMs;

    public ShiftableTimeProvider(string offsetFile)
    {
        _offsetFile = offsetFile;
        Read();
        _timer = new Timer(_ => Read(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public long OffsetMs => Interlocked.Read(ref _offsetMs);

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddMilliseconds(OffsetMs);

    private void Read()
    {
        try
        {
            if (File.Exists(_offsetFile) && Int64.TryParse(File.ReadAllText(_offsetFile).Trim(), out long offset))
                Interlocked.Exchange(ref _offsetMs, offset);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => _timer.Dispose();
}
