using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Foundatio.Utility;

public class InstrumentsValues<T1, T2, T3>
                where T1 : struct
                where T2 : struct
                where T3 : struct
{
    private readonly object _lock = new();
    private int _readCount;
    private bool _valuesUpdating;
    private readonly AutoResetEvent _valuesUpdatingEvent = new(false);
    private T1? _value1;
    private T2? _value2;
    private T3? _value3;
    private readonly Func<(T1, T2, T3)> _readValuesFunc;
    private readonly ILogger _logger;

    public InstrumentsValues(Func<(T1, T2, T3)> readValuesFunc, ILogger logger)
    {
        _readValuesFunc = readValuesFunc;
        _logger = logger;
    }

    private void EnsureValues()
    {
        lock (_lock)
        {
            if (_readCount == 0) {
                _logger.LogDebug("Getting values");
                (_value1, _value2, _value3) = _readValuesFunc();
            }

            // get values every 3 reads
            if (_readCount == 2)
                _readCount = 0;
            else
                _readCount++;
        }
    }

    public T1 GetValue1()
    {
        EnsureValues();

        return _value1 ?? default;
    }

    public T2 GetValue2()
    {
        EnsureValues();

        return _value2 ?? default;
    }

    public T3 GetValue3()
    {
        EnsureValues();

        return _value3 ?? default;
    }
}
