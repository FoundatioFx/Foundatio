using System;

namespace Foundatio.Metrics {
    public interface IMetricsClient {
        void Counter(string statName, int value = 1);
        void Gauge(string statName, double value);
        void Timer(string statName, long milliseconds);
        IDisposable StartTimer(string statName);
        void Time(Action action, string statName);
        T Time<T>(Func<T> func, string statName);
    }

    public interface IMetricsClient2 {
        void Counter(string statName, int value = 1);
        void Gauge(string statName, double value);
        void Timer(string statName, long milliseconds);
    }
}