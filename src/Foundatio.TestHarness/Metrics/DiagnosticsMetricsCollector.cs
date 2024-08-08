using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Metrics;

public class DiagnosticsMetricsCollector : IDisposable
{
    private readonly Timer _timer;
    private readonly MeterListener _meterListener = new();
    private readonly ConcurrentQueue<RecordedMeasurement<byte>> _byteMeasurements = new();
    private readonly ConcurrentQueue<RecordedMeasurement<short>> _shortMeasurements = new();
    private readonly ConcurrentQueue<RecordedMeasurement<int>> _intMeasurements = new();
    private readonly ConcurrentQueue<RecordedMeasurement<long>> _longMeasurements = new();
    private readonly ConcurrentQueue<RecordedMeasurement<float>> _floatMeasurements = new();
    private readonly ConcurrentQueue<RecordedMeasurement<double>> _doubleMeasurements = new();
    private readonly ConcurrentQueue<RecordedMeasurement<decimal>> _decimalMeasurements = new();
    private readonly int _maxMeasurementCountPerType;
    private readonly AsyncAutoResetEvent _measurementEvent = new(false);
    private readonly ILogger _logger;

    public DiagnosticsMetricsCollector(string metricNameOrPrefix, ILogger logger, int maxMeasurementCountPerType = 100000) : this(n => n.StartsWith(metricNameOrPrefix), logger, maxMeasurementCountPerType) { }

    public DiagnosticsMetricsCollector(Func<string, bool> shouldCollect, ILogger logger, int maxMeasurementCount = 100000)
    {
        _logger = logger;
        _maxMeasurementCountPerType = maxMeasurementCount;

        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (shouldCollect(instrument.Meter.Name))
                listener.EnableMeasurementEvents(instrument);
        };

        _meterListener.SetMeasurementEventCallback<byte>((instrument, measurement, tags, state) =>
        {
            _byteMeasurements.Enqueue(new RecordedMeasurement<byte>(instrument, measurement, ref tags, state));
            if (_byteMeasurements.Count > _maxMeasurementCountPerType)
                _byteMeasurements.TryDequeue(out _);
            _measurementEvent.Set();
        });

        _meterListener.SetMeasurementEventCallback<short>((instrument, measurement, tags, state) =>
        {
            _shortMeasurements.Enqueue(new RecordedMeasurement<short>(instrument, measurement, ref tags, state));
            if (_shortMeasurements.Count > _maxMeasurementCountPerType)
                _shortMeasurements.TryDequeue(out _);
            _measurementEvent.Set();
        });

        _meterListener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
        {
            _intMeasurements.Enqueue(new RecordedMeasurement<int>(instrument, measurement, ref tags, state));
            if (_intMeasurements.Count > _maxMeasurementCountPerType)
                _intMeasurements.TryDequeue(out _);
            _measurementEvent.Set();
        });

        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            _longMeasurements.Enqueue(new RecordedMeasurement<long>(instrument, measurement, ref tags, state));
            if (_longMeasurements.Count > _maxMeasurementCountPerType)
                _longMeasurements.TryDequeue(out _);
            _measurementEvent.Set();
        });

        _meterListener.SetMeasurementEventCallback<float>((instrument, measurement, tags, state) =>
        {
            _floatMeasurements.Enqueue(new RecordedMeasurement<float>(instrument, measurement, ref tags, state));
            if (_floatMeasurements.Count > _maxMeasurementCountPerType)
                _floatMeasurements.TryDequeue(out _);
            _measurementEvent.Set();
        });

        _meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            _doubleMeasurements.Enqueue(new RecordedMeasurement<double>(instrument, measurement, ref tags, state));
            if (_doubleMeasurements.Count > _maxMeasurementCountPerType)
                _doubleMeasurements.TryDequeue(out _);
            _measurementEvent.Set();
        });

        _meterListener.SetMeasurementEventCallback<decimal>((instrument, measurement, tags, state) =>
        {
            _decimalMeasurements.Enqueue(new RecordedMeasurement<decimal>(instrument, measurement, ref tags, state));
            if (_decimalMeasurements.Count > _maxMeasurementCountPerType)
                _decimalMeasurements.TryDequeue(out _);
            _measurementEvent.Set();
        });

        _meterListener.Start();

        _timer = new Timer(_ => RecordObservableInstruments(), null, TimeSpan.Zero,  TimeSpan.FromMilliseconds(50));
    }

    public void RecordObservableInstruments()
    {
        _meterListener.RecordObservableInstruments();
    }

    public IReadOnlyCollection<RecordedMeasurement<T>> GetMeasurements<T>(string name = null) where T : struct
    {
        if (typeof(T) == typeof(byte))
        {
            if (name == null)
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_byteMeasurements);
            else
                return ImmutableList.CreateRange(((IEnumerable<RecordedMeasurement<T>>)_byteMeasurements).Where(m => m.Name == name));
        }
        else if (typeof(T) == typeof(short))
        {
            if (name == null)
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_shortMeasurements);
            else
                return ImmutableList.CreateRange(((IEnumerable<RecordedMeasurement<T>>)_shortMeasurements).Where(m => m.Name == name));
        }
        else if (typeof(T) == typeof(int))
        {
            if (name == null)
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_intMeasurements);
            else
                return ImmutableList.CreateRange(((IEnumerable<RecordedMeasurement<T>>)_intMeasurements).Where(m => m.Name == name));
        }
        else if (typeof(T) == typeof(long))
        {
            if (name == null)
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_longMeasurements);
            else
                return ImmutableList.CreateRange(((IEnumerable<RecordedMeasurement<T>>)_longMeasurements).Where(m => m.Name == name));
        }
        else if (typeof(T) == typeof(float))
        {
            if (name == null)
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_floatMeasurements);
            else
                return ImmutableList.CreateRange(((IEnumerable<RecordedMeasurement<T>>)_floatMeasurements).Where(m => m.Name == name));
        }
        else if (typeof(T) == typeof(double))
        {
            if (name == null)
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_doubleMeasurements);
            else
                return ImmutableList.CreateRange(((IEnumerable<RecordedMeasurement<T>>)_doubleMeasurements).Where(m => m.Name == name));
        }
        else if (typeof(T) == typeof(decimal))
        {
            if (name == null)
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_decimalMeasurements);
            else
                return ImmutableList.CreateRange(((IEnumerable<RecordedMeasurement<T>>)_decimalMeasurements).Where(m => m.Name == name));
        }
        else
        {
            return ImmutableList.Create<RecordedMeasurement<T>>();
        }

        // byte, short, int, long, float, double, decimal
    }

    public int GetCount<T>(string name) where T : struct
    {
        return GetMeasurements<T>().Count(m => m.Name == name);
    }

    public double GetSum<T>(string name) where T : struct
    {
        if (typeof(T) == typeof(byte))
        {
            var measurements = GetMeasurements<byte>(name);
            return measurements.Sum(m => m.Value);
        }
        else if (typeof(T) == typeof(short))
        {
            var measurements = GetMeasurements<short>(name);
            return measurements.Sum(m => m.Value);
        }
        else if (typeof(T) == typeof(int))
        {
            var measurements = GetMeasurements<int>(name);
            return measurements.Sum(m => m.Value);
        }
        else if (typeof(T) == typeof(long))
        {
            var measurements = GetMeasurements<long>(name);
            return measurements.Sum(m => m.Value);
        }
        else if (typeof(T) == typeof(float))
        {
            var measurements = GetMeasurements<float>(name);
            return measurements.Sum(m => m.Value);
        }
        else if (typeof(T) == typeof(double))
        {
            var measurements = GetMeasurements<double>(name);
            return measurements.Sum(m => m.Value);
        }
        else if (typeof(T) == typeof(decimal))
        {
            var measurements = GetMeasurements<decimal>(name);
            return measurements.Sum(m => (double)m.Value);
        }
        else
        {
            return 0;
        }
    }

    public double GetAvg<T>(string name) where T : struct
    {
        if (typeof(T) == typeof(byte))
        {
            var measurements = GetMeasurements<byte>(name);
            return measurements.Average(m => m.Value);
        }
        else if (typeof(T) == typeof(short))
        {
            var measurements = GetMeasurements<short>(name);
            return measurements.Average(m => m.Value);
        }
        else if (typeof(T) == typeof(int))
        {
            var measurements = GetMeasurements<int>(name);
            return measurements.Average(m => m.Value);
        }
        else if (typeof(T) == typeof(long))
        {
            var measurements = GetMeasurements<long>(name);
            return measurements.Average(m => m.Value);
        }
        else if (typeof(T) == typeof(float))
        {
            var measurements = GetMeasurements<float>(name);
            return measurements.Average(m => m.Value);
        }
        else if (typeof(T) == typeof(double))
        {
            var measurements = GetMeasurements<double>(name);
            return measurements.Average(m => m.Value);
        }
        else if (typeof(T) == typeof(decimal))
        {
            var measurements = GetMeasurements<decimal>(name);
            return measurements.Average(m => (double)m.Value);
        }
        else
        {
            return 0;
        }
    }

    public double GetMax<T>(string name) where T : struct
    {
        if (typeof(T) == typeof(byte))
        {
            var measurements = GetMeasurements<byte>(name);
            return measurements.Max(m => m.Value);
        }
        else if (typeof(T) == typeof(short))
        {
            var measurements = GetMeasurements<short>(name);
            return measurements.Max(m => m.Value);
        }
        else if (typeof(T) == typeof(int))
        {
            var measurements = GetMeasurements<int>(name);
            return measurements.Max(m => m.Value);
        }
        else if (typeof(T) == typeof(long))
        {
            var measurements = GetMeasurements<long>(name);
            return measurements.Max(m => m.Value);
        }
        else if (typeof(T) == typeof(float))
        {
            var measurements = GetMeasurements<float>(name);
            return measurements.Max(m => m.Value);
        }
        else if (typeof(T) == typeof(double))
        {
            var measurements = GetMeasurements<double>(name);
            return measurements.Max(m => m.Value);
        }
        else if (typeof(T) == typeof(decimal))
        {
            var measurements = GetMeasurements<decimal>(name);
            return measurements.Max(m => (double)m.Value);
        }
        else
        {
            return 0;
        }
    }

    public async Task<bool> WaitForCounterAsync<T>(string statName, long count = 1, TimeSpan? timeout = null) where T : struct
    {
        using var cancellationTokenSource = timeout.ToCancellationTokenSource(TimeSpan.FromMinutes(1));
        return await WaitForCounterAsync<T>(statName, () => Task.CompletedTask, count, cancellationTokenSource.Token).AnyContext();
    }

    public async Task<bool> WaitForCounterAsync<T>(string name, Func<Task> work, long count = 1, CancellationToken cancellationToken = default) where T : struct
    {
        if (count <= 0)
            return true;

        if (cancellationToken == default)
        {
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            cancellationToken = cancellationTokenSource.Token;
        }

        var start = DateTime.UtcNow;

        var currentCount = (int)GetSum<T>(name);
        var targetCount = currentCount + count;

        if (work != null)
            await work().AnyContext();

        _logger.LogTrace("Wait: count={Count}", count);
        currentCount = (int)GetSum<T>(name);

        while (!cancellationToken.IsCancellationRequested && currentCount < targetCount)
        {
            try
            {
                await _measurementEvent.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { }
            currentCount = (int)GetSum<T>(name);
            _logger.LogTrace("Got new measurement: count={CurrentCount} expected={Count}", currentCount, targetCount);
        }

        _logger.LogTrace("Done waiting: count={CurrentCount} expected={Count} success={Success} time={Time}", currentCount, targetCount, currentCount >= targetCount, DateTime.UtcNow.Subtract(start));

        return currentCount >= targetCount;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _timer.Dispose();
        _meterListener?.Dispose();
    }
}

[DebuggerDisplay("{Name}={Value}")]
public struct RecordedMeasurement<T> where T : struct
{
    public RecordedMeasurement(Instrument instrument, T value, ref ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
    {
        Instrument = instrument;
        Name = Instrument.Name;
        Value = value;
        if (tags.Length > 0)
            Tags = ImmutableDictionary.CreateRange(tags.ToArray());
        else
            Tags = ImmutableDictionary<string, object>.Empty;
        State = state;
    }

    public Instrument Instrument { get; }
    public string Name { get; }
    public T Value { get; }
    public IReadOnlyDictionary<string, object> Tags { get; }
    public object State { get; }
}
