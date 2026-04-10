using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

public class SharedMessageBusOptions : SharedOptions
{
    /// <summary>
    /// The topic name
    /// </summary>
    public string Topic { get; set; } = "messages";

    /// <summary>
    /// Controls which types messages are mapped to.
    /// </summary>
    public Dictionary<string, Type> MessageTypeMappings { get; set; } = new();
}

public class SharedMessageBusOptionsBuilder<TOptions, TBuilder> : SharedOptionsBuilder<TOptions, TBuilder>
    where TOptions : SharedMessageBusOptions, new()
    where TBuilder : SharedMessageBusOptionsBuilder<TOptions, TBuilder>, new()
{
    public TBuilder Topic(string topic)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);

        Target.Topic = topic;
        return (TBuilder)this;
    }

    public TBuilder MapMessageType<T>(string name)
    {
        Target.MessageTypeMappings[name] = typeof(T);
        return (TBuilder)this;
    }

    public TBuilder MapMessageTypeToClassName<T>()
    {
        Target.MessageTypeMappings[typeof(T).Name] = typeof(T);
        return (TBuilder)this;
    }
}
