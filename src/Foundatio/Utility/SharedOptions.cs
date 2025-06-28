using System;
using Foundatio.Utility.Resilience;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio;

public class SharedOptions
{
    public IResiliencePipelineProvider ResiliencePipelineProvider { get; set; } = new FoundatioResiliencePipelineProvider();
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    public ISerializer Serializer { get; set; }
    public ILoggerFactory LoggerFactory { get; set; }
}

public class SharedOptionsBuilder<TOption, TBuilder> : OptionsBuilder<TOption>
    where TOption : SharedOptions, new()
    where TBuilder : SharedOptionsBuilder<TOption, TBuilder>
{
    public TBuilder ResiliencePipelineProvider(IResiliencePipelineProvider resiliencePipelineProvider)
    {
        Target.ResiliencePipelineProvider = resiliencePipelineProvider ?? throw new ArgumentNullException(nameof(resiliencePipelineProvider));
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
