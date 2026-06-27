using System;

namespace Foundatio.Messaging;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class MessageRouteAttribute : Attribute
{
    public MessageRouteAttribute()
    {
    }

    public MessageRouteAttribute(string name)
    {
        Destination = name;
        Topic = name;
    }

    public string? Destination { get; set; }
    public string? Topic { get; set; }
    public string? Subscription { get; set; }
}
