using System;
using System.Threading.Tasks;

namespace Foundatio.StatsD {
    public interface IStatsDClient : IDisposable {
        Task CounterAsync(string statName, int value = 1);
        Task GaugeAsync(string statName, double value);
        Task TimerAsync(string statName, long milliseconds);
    }
}