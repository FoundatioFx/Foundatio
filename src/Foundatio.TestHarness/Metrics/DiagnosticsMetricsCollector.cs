using System;
using System.Diagnostics.Metrics;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Threading;
using Foundatio.AsyncEx;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Metrics {
    public class DiagnosticsMetricsCollector : IDisposable {
        private readonly MeterListener _meterListener = new();
        private readonly Queue<RecordedMeasurement<byte>> _byteMeasurements = new();
        private readonly Queue<RecordedMeasurement<short>> _shortMeasurements = new();
        private readonly Queue<RecordedMeasurement<int>> _intMeasurements = new();
        private readonly Queue<RecordedMeasurement<long>> _longMeasurements = new();
        private readonly Queue<RecordedMeasurement<float>> _floatMeasurements = new();
        private readonly Queue<RecordedMeasurement<double>> _doubleMeasurements = new();
        private readonly Queue<RecordedMeasurement<decimal>> _decimalMeasurements = new();
        private readonly int _maxMeasurementCountPerType = 1000;
        private readonly AsyncAutoResetEvent _measurementEvent = new(false);
        private readonly ILogger _logger;

        public DiagnosticsMetricsCollector(string metricNameOrPrefix, ILogger logger, int maxMeasurementCountPerType = 1000) : this(n => n.StartsWith(metricNameOrPrefix), logger, maxMeasurementCountPerType) {}

        public DiagnosticsMetricsCollector(Func<string, bool> shouldCollect, ILogger logger, int maxMeasurementCount = 1000) {
            _logger = logger;
            _maxMeasurementCountPerType = maxMeasurementCount;

            _meterListener.InstrumentPublished = (instrument, listener) => {
                if (shouldCollect(instrument.Meter.Name))
                    listener.EnableMeasurementEvents(instrument);
            };

            _meterListener.SetMeasurementEventCallback<byte>((instrument, measurement, tags, state) => {
                _byteMeasurements.Enqueue(new RecordedMeasurement<byte>(instrument, measurement, ref tags, state));
                if (_byteMeasurements.Count > _maxMeasurementCountPerType)
                    _byteMeasurements.Dequeue();
                _measurementEvent.Set();
            });

            _meterListener.SetMeasurementEventCallback<short>((instrument, measurement, tags, state) => {
                _shortMeasurements.Enqueue(new RecordedMeasurement<short>(instrument, measurement, ref tags, state));
                if (_shortMeasurements.Count > _maxMeasurementCountPerType)
                    _shortMeasurements.Dequeue();
                _measurementEvent.Set();
            });

            _meterListener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) => {
                _intMeasurements.Enqueue(new RecordedMeasurement<int>(instrument, measurement, ref tags, state));
                if (_intMeasurements.Count > _maxMeasurementCountPerType)
                    _intMeasurements.Dequeue();
                _measurementEvent.Set();
            });

            _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => {
                _longMeasurements.Enqueue(new RecordedMeasurement<long>(instrument, measurement, ref tags, state));
                if (_longMeasurements.Count > _maxMeasurementCountPerType)
                    _longMeasurements.Dequeue();
                _measurementEvent.Set();
            });

            _meterListener.SetMeasurementEventCallback<float>((instrument, measurement, tags, state) => {
                _floatMeasurements.Enqueue(new RecordedMeasurement<float>(instrument, measurement, ref tags, state));
                if (_floatMeasurements.Count > _maxMeasurementCountPerType)
                    _floatMeasurements.Dequeue();
                _measurementEvent.Set();
            });

            _meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) => {
                _doubleMeasurements.Enqueue(new RecordedMeasurement<double>(instrument, measurement, ref tags, state));
                if (_doubleMeasurements.Count > _maxMeasurementCountPerType)
                    _doubleMeasurements.Dequeue();
                _measurementEvent.Set();
            });

            _meterListener.SetMeasurementEventCallback<decimal>((instrument, measurement, tags, state) => {
                _decimalMeasurements.Enqueue(new RecordedMeasurement<decimal>(instrument, measurement, ref tags, state));
                if (_decimalMeasurements.Count > _maxMeasurementCountPerType)
                    _decimalMeasurements.Dequeue();
                _measurementEvent.Set();
            });

            _meterListener.Start();
        }

        public void RecordObservableInstruments() {
            _meterListener.RecordObservableInstruments();
        }

        public IReadOnlyCollection<RecordedMeasurement<T>> GetMeasurements<T>() where T: struct {
            if (typeof(T) == typeof(byte))
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_byteMeasurements);
            else if (typeof(T) == typeof(short))
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_shortMeasurements);
            else if (typeof(T) == typeof(int))
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_intMeasurements);
            else if (typeof(T) == typeof(long))
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_longMeasurements);
            else if (typeof(T) == typeof(float))
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_floatMeasurements);
            else if (typeof(T) == typeof(double))
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_doubleMeasurements);
            else if (typeof(T) == typeof(decimal))
                return ImmutableList.CreateRange((IEnumerable<RecordedMeasurement<T>>)_decimalMeasurements);
            else
                return ImmutableList.Create<RecordedMeasurement<T>>();

            // byte, short, int, long, float, double, decimal
        }

        public int GetCount<T>(string name) where T: struct {
            return GetMeasurements<T>().Count(m => m.Name == name);
        }

        public double GetSum<T>(string name) where T : struct {
            if (typeof(T) == typeof(byte))
                return GetMeasurements<byte>().OfType<RecordedMeasurement<byte>>().Where(m => m.Name == name).Sum(m => m.Value);
            else if (typeof(T) == typeof(short))
                return GetMeasurements<short>().OfType<RecordedMeasurement<short>>().Where(m => m.Name == name).Sum(m => m.Value);
            else if (typeof(T) == typeof(int))
                return GetMeasurements<int>().OfType<RecordedMeasurement<int>>().Where(m => m.Name == name).Sum(m => m.Value);
            else if (typeof(T) == typeof(long))
                return GetMeasurements<long>().OfType<RecordedMeasurement<long>>().Where(m => m.Name == name).Sum(m => m.Value);
            else if (typeof(T) == typeof(float))
                return GetMeasurements<float>().OfType<RecordedMeasurement<float>>().Where(m => m.Name == name).Sum(m => m.Value);
            else if (typeof(T) == typeof(double))
                return GetMeasurements<double>().OfType<RecordedMeasurement<double>>().Where(m => m.Name == name).Sum(m => m.Value);
            else if (typeof(T) == typeof(decimal))
                return GetMeasurements<decimal>().OfType<RecordedMeasurement<decimal>>().Where(m => m.Name == name).Sum(m => (double)m.Value);
            else
                return 0;
        }

        public double GetAvg<T>(string name) where T : struct {
            if (typeof(T) == typeof(byte))
                return GetMeasurements<byte>().OfType<RecordedMeasurement<byte>>().Where(m => m.Name == name).Average(m => m.Value);
            else if (typeof(T) == typeof(short))
                return GetMeasurements<short>().OfType<RecordedMeasurement<short>>().Where(m => m.Name == name).Average(m => m.Value);
            else if (typeof(T) == typeof(int))
                return GetMeasurements<int>().OfType<RecordedMeasurement<int>>().Where(m => m.Name == name).Average(m => m.Value);
            else if (typeof(T) == typeof(long))
                return GetMeasurements<long>().OfType<RecordedMeasurement<long>>().Where(m => m.Name == name).Average(m => m.Value);
            else if (typeof(T) == typeof(float))
                return GetMeasurements<float>().OfType<RecordedMeasurement<float>>().Where(m => m.Name == name).Average(m => m.Value);
            else if (typeof(T) == typeof(double))
                return GetMeasurements<double>().OfType<RecordedMeasurement<double>>().Where(m => m.Name == name).Average(m => m.Value);
            else if (typeof(T) == typeof(decimal))
                return GetMeasurements<decimal>().OfType<RecordedMeasurement<decimal>>().Where(m => m.Name == name).Average(m => (double)m.Value);
            else
                return 0;
        }

        public async Task<bool> WaitForCounterAsync<T>(string statName, long count = 1, TimeSpan? timeout = null) where T: struct {
            using var cancellationTokenSource = timeout.ToCancellationTokenSource(TimeSpan.FromMinutes(1));
            return await WaitForCounterAsync<T>(statName, () => Task.CompletedTask, count, cancellationTokenSource.Token).AnyContext();
        }

        public async Task<bool> WaitForCounterAsync<T>(string name, Func<Task> work, long count = 1, CancellationToken cancellationToken = default) where T : struct {
            if (count <= 0)
                return true;

            if (cancellationToken == default) {
                using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                cancellationToken = cancellationTokenSource.Token;
            }

            var start = SystemClock.UtcNow;

            var currentCount = (int)GetSum<T>(name);
            var targetCount = currentCount + count;

            if (work != null)
                await work().AnyContext();

            _logger.LogTrace("Wait: count={Count}", count);
            currentCount = (int)GetSum<T>(name);

            while (!cancellationToken.IsCancellationRequested && currentCount < targetCount) {
                try {
                    await _measurementEvent.WaitAsync(cancellationToken);
                } catch (OperationCanceledException) { }
                currentCount = (int)GetSum<T>(name);
                _logger.LogTrace("Got new measurement: count={CurrentCount} expected={Count}", currentCount, targetCount);
            }

            _logger.LogTrace("Done waiting: count={CurrentCount} expected={Count} success={Success} time={Time}", currentCount, targetCount, currentCount >= targetCount, SystemClock.UtcNow.Subtract(start));

            return currentCount >= targetCount;
        }

        public void Dispose() {
            GC.SuppressFinalize(this);
            _meterListener?.Dispose();
        }
    }

    [DebuggerDisplay("{Name}={Value}")]
    public struct RecordedMeasurement<T> where T : struct {
        public RecordedMeasurement(Instrument instrument, T value, ref ReadOnlySpan<KeyValuePair<string, object>> tags, object state) {
            Instrument = instrument;
            Value = value;
            if (tags.Length > 0)
                Tags = ImmutableDictionary.CreateRange(tags.ToArray());
            else
                Tags = ImmutableDictionary<string, object>.Empty;
            State = state;
        }

        public Instrument Instrument { get; }
        public string Name => Instrument.Name;
        public T Value { get; }
        public IReadOnlyDictionary<string, object> Tags { get; }
        public object State { get; }
    }
}