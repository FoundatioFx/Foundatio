using System;
using System.Diagnostics.Metrics;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace Foundatio.Tests.Metrics {
    public class DiagnosticsMetricsCollector : IDisposable {
        private readonly MeterListener _meterListener = new();
        private readonly List<RecordedMeasurement<int>> _intMeasurements = new();
        private readonly List<RecordedMeasurement<double>> _doubleMeasurements = new();
        private readonly List<RecordedMeasurement<long>> _longMeasurements = new();

        public DiagnosticsMetricsCollector(string metricNameOrPrefix) : this(n => n.StartsWith(metricNameOrPrefix)) {}

        public DiagnosticsMetricsCollector(Func<string, bool> shouldCollect) {
            _meterListener.InstrumentPublished = (instrument, listener) => {
                if (shouldCollect(instrument.Meter.Name))
                    listener.EnableMeasurementEvents(instrument);
            };

            _meterListener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) => {
                _intMeasurements.Add(new RecordedMeasurement<int> { Name = instrument.Name, Value = measurement });
            });

            _meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) => {
                _doubleMeasurements.Add(new RecordedMeasurement<double> { Name = instrument.Name, Value = measurement });
            });

            _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => {
                _longMeasurements.Add(new RecordedMeasurement<long> { Name = instrument.Name, Value = measurement });
            });

            _meterListener.Start();
        }

        public void RecordObservableInstruments() {
            _meterListener.RecordObservableInstruments();
        }

        public ICollection<RecordedMeasurement<int>> IntMeasurements => _intMeasurements;
        public int GetIntCount(string name) {
            return _intMeasurements.Count(m => m.Name == name);
        }
        public int GetIntSum(string name) {
            return _intMeasurements.Where(m => m.Name == name).Sum(m => m.Value);
        }
        public double GetIntAvg(string name) {
            return _intMeasurements.Where(m => m.Name == name).Average(m => m.Value);
        }

        public ICollection<RecordedMeasurement<double>> DoubleMeasurements => _doubleMeasurements;
        public int GetDoubleCount(string name) {
            return _doubleMeasurements.Count(m => m.Name == name);
        }
        public double GetDoubleSum(string name) {
            return _doubleMeasurements.Where(m => m.Name == name).Sum(m => m.Value);
        }
        public double GetDoubleAvg(string name) {
            return _doubleMeasurements.Where(m => m.Name == name).Average(m => m.Value);
        }

        public ICollection<RecordedMeasurement<long>> LongMeasurements => _longMeasurements;
        public long GetLongCount(string name) {
            return _longMeasurements.Count(m => m.Name == name);
        }
        public long GetLongSum(string name) {
            return _longMeasurements.Where(m => m.Name == name).Sum(m => m.Value);
        }
        public double GetLongAvg(string name) {
            return _longMeasurements.Where(m => m.Name == name).Average(m => m.Value);
        }

        public void Dispose() {
            _meterListener?.Dispose();
        }
    }

    [DebuggerDisplay("{Name}={Value}")]
    public class RecordedMeasurement<T> where T : struct {
        public string Name { get; set; }
        public T Value { get; set; }
    }
}