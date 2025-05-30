using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Foundatio.Jobs;

namespace Foundatio.Extensions.Hosting.Jobs;

public class ScheduledJobOptions : INotifyPropertyChanged
{
    private string _name;
    private string _description;
    private Func<IServiceProvider, IJob> _jobFactory;
    private bool _waitForStartupActions;
    private string _cronSchedule;
    private TimeZoneInfo _cronTimeZone;
    private bool _isDistributed;
    private bool _isEnabled = true;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    public Func<IServiceProvider, IJob> JobFactory
    {
        get => _jobFactory;
        set => SetField(ref _jobFactory, value);
    }

    public bool WaitForStartupActions
    {
        get => _waitForStartupActions;
        set => SetField(ref _waitForStartupActions, value);
    }

    public string CronSchedule
    {
        get => _cronSchedule;
        set => SetField(ref _cronSchedule, value);
    }

    public TimeZoneInfo CronTimeZone
    {
        get => _cronTimeZone;
        set => SetField(ref _cronTimeZone, value);
    }

    public bool IsDistributed
    {
        get => _isDistributed;
        set => SetField(ref _isDistributed, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

