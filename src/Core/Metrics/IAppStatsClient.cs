using System;

namespace Foundatio.AppStats {
    public interface IAppStatsClient {
        void Counter(string statName, int value = 1);

        void Gauge(string statName, double value);

        void Timer(string statName, long milliseconds);

        IDisposable StartTimer(string statName);

        void Time(Action action, string statName);

        T Time<T>(Func<T> func, string statName);
    }
}