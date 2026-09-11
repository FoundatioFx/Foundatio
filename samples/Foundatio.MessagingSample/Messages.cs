using Foundatio.Messaging;

namespace Foundatio.MessagingSample;

/// <summary>
/// A command / unit of work, delivered with <c>bus.SendAsync</c> — running instances compete for deliveries. Handlers must tolerate redelivery.
/// The <see cref="MessageRouteAttribute"/> names the destination ("orders"); without it the kebab-cased type name
/// ("process-order") is used.
/// </summary>
[MessageRoute("orders")]
public class ProcessOrder
{
    public string Product { get; set; } = "";
    public int Quantity { get; set; } = 1;
}

/// <summary>
/// An event, delivered with <c>bus.PublishAsync</c> — each subscribing service receives one copy (and this sample's
/// handler uses the durable announcements group, whose replicas compete).
/// </summary>
[MessageRoute("announcements")]
public class Announcement
{
    public string Text { get; set; } = "";
}
