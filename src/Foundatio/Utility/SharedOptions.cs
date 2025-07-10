using System;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio;

public class SharedOptions
{
    private IResiliencePolicyProvider _policyProvider;
    private TimeProvider _timeProvider;
    private ISerializer _serializer;
    private ILoggerFactory _loggerFactory;

    internal bool HasResiliencePolicyProvider => _policyProvider != null;
    public IResiliencePolicyProvider ResiliencePolicyProvider
    {
        get => _policyProvider ?? DefaultResiliencePolicyProvider.Instance;
        set => _policyProvider = value;
    }

    internal bool HasTimeProvider => _timeProvider != null;
    public TimeProvider TimeProvider
    {
        get => _timeProvider ?? TimeProvider.System;
        set => _timeProvider = value;
    }

    internal bool HasSerializer => _serializer != null;
    public ISerializer Serializer
    {
        get => _serializer ?? DefaultSerializer.Instance;
        set => _serializer = value;
    }

    internal bool HasLoggerFactory => _loggerFactory != null;
    public ILoggerFactory LoggerFactory
    {
        get => _loggerFactory ?? NullLoggerFactory.Instance;
        set => _loggerFactory = value;
    }
}

public class SharedOptionsBuilder<TOption, TBuilder> : OptionsBuilder<TOption>
    where TOption : SharedOptions, new()
    where TBuilder : SharedOptionsBuilder<TOption, TBuilder>, new()
{
    public TBuilder ResiliencePipelineProvider(IResiliencePolicyProvider resiliencePolicyProvider)
    {
        Target.ResiliencePolicyProvider = resiliencePolicyProvider ?? throw new ArgumentNullException(nameof(resiliencePolicyProvider));
        return (TBuilder)this;
    }

    public TBuilder UseServices(IServiceProvider serviceProvider, bool overrideExisting = false)
    {
        Target.UseServices(serviceProvider, overrideExisting);
        return (TBuilder)this;
    }

    public TBuilder Configure(Builder<TBuilder, TOption> builder)
    {
        builder.Invoke((TBuilder)this);
        return (TBuilder)this;
    }

    public TBuilder TimeProvider(TimeProvider timeProvider)
    {
        Target.TimeProvider = timeProvider;
        return (TBuilder)this;
    }

    public TBuilder Serializer(ISerializer serializer)
    {
        Target.Serializer = serializer;
        return (TBuilder)this;
    }

    public TBuilder LoggerFactory(ILoggerFactory loggerFactory)
    {
        Target.LoggerFactory = loggerFactory;
        return (TBuilder)this;
    }
}

public static class SharedOptionsExtensions
{
    public static TOption UseServices<TOption>(this TOption options, IServiceProvider serviceProvider, bool overrideExisting = false)
        where TOption : SharedOptions, new()
    {
        options ??= new TOption();

        if (overrideExisting)
        {
            options.ResiliencePolicyProvider = serviceProvider.GetService<IResiliencePolicyProvider>();
            options.TimeProvider = serviceProvider.GetService<TimeProvider>();
            options.Serializer = serviceProvider.GetService<ISerializer>();
            options.LoggerFactory = serviceProvider.GetService<ILoggerFactory>();
        }
        else
        {
            if (!options.HasResiliencePolicyProvider)
                options.ResiliencePolicyProvider = serviceProvider.GetService<IResiliencePolicyProvider>();
            if (!options.HasTimeProvider)
                options.TimeProvider = serviceProvider.GetService<TimeProvider>();
            if (!options.HasSerializer)
                options.Serializer = serviceProvider.GetService<ISerializer>();
            if (!options.HasLoggerFactory)
                options.LoggerFactory = serviceProvider.GetService<ILoggerFactory>();
        }

        return options;
    }
}
