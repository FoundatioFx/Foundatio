using System;
using System.Threading.Tasks;

namespace Foundatio.Metrics {
    public interface IMetricsClient : IDisposable {
        void Counter(string statName, int value = 1);
        void Gauge(string statName, double value);
        void Timer(string statName, long milliseconds);
        IDisposable StartTimer(string statName);
        void Time(Action action, string statName);
        T Time<T>(Func<T> func, string statName);
    }

    public interface IMetricsClient2 {
        Task CounterAsync(string statName, int value = 1);
        Task GaugeAsync(string statName, double value);
        Task TimerAsync(string statName, long milliseconds);
    }
}