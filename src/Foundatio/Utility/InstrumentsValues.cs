using System;

namespace Foundatio.Utility;

public class InstrumentsValues<T1, T2, T3>
                where T1 : struct
                where T2 : struct
                where T3 : struct
{
    private T1? _value1;
    private T2? _value2;
    private T3? _value3;
    private Func<(T1, T2, T3)> UpdateValues { get; set; }

    public InstrumentsValues(Func<(T1, T2, T3)> readValues)
    {
        _value1 = null;
        _value2 = null;
        _value3 = null;

        UpdateValues = readValues;
    }

    public T1 GetValue1()
    {
        if (!_value1.HasValue)
            (_value1, _value2, _value3) = UpdateValues();

        if (_value1 == null)
            return default;

        T1 value = _value1.Value;
        _value1 = null;
        return value;

    }

    public T2 GetValue2()
    {
        if (!_value2.HasValue)
            (_value1, _value2, _value3) = UpdateValues();

        if (_value2 == null)
            return default;

        T2 value = _value2.Value;
        _value2 = null;
        return value;

    }

    public T3 GetValue3()
    {
        if (!_value3.HasValue)
            (_value1, _value2, _value3) = UpdateValues();

        if (_value3 == null)
            return default;

        T3 value = _value3.Value;
        _value3 = null;
        return value;
    }
}
