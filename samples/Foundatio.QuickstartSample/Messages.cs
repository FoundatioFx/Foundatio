namespace Foundatio.QuickstartSample;

/// <summary>
/// An EVENT — published with <c>bus.PublishAsync</c>: every subscribing service receives one copy.
/// </summary>
public record OrderPlaced(int OrderId, string Product);

/// <summary>
/// A COMMAND / unit of work — sent with <c>bus.SendAsync</c>: exactly one handler instance across the fleet
/// processes each one (competing consumers).
/// </summary>
public record SendReceipt(int OrderId, string Email);
