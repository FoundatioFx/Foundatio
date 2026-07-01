using Foundatio.Messaging;

namespace Foundatio.MessagingSample;

/// <summary>
/// A unit of work processed off a queue. The <see cref="MessageRouteAttribute"/> names the destination ("orders");
/// with competing consumers, each order is handled by exactly one running instance.
/// </summary>
[MessageRoute("orders")]
public class ProcessOrder
{
    public string Product { get; set; } = "";
    public int Quantity { get; set; } = 1;
}

/// <summary>
/// A broadcast event published to a topic ("announcements"). With a per-instance subscription, every running instance
/// receives its own copy.
/// </summary>
[MessageRoute("announcements")]
public class Announcement
{
    public string Text { get; set; } = "";
}
