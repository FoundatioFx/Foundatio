using Foundatio.Messaging;
using Microsoft.Extensions.Logging;

namespace Foundatio.QuickstartSample;

/// <summary>
/// Handles the <see cref="OrderPlaced"/> event. Registration carries no topology decision — this receives events
/// because Program.cs calls <c>bus.PublishAsync</c>. Resolved from DI in its own scope per message; throwing here
/// would trigger the core retry/dead-letter policy.
/// </summary>
public sealed class OrderPlacedHandler(ILogger<OrderPlacedHandler> logger) : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(IMessageContext<OrderPlaced> context, CancellationToken cancellationToken)
    {
        logger.LogInformation("EVENT handled: order {OrderId} placed for {Product}", context.Message.OrderId, context.Message.Product);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Handles the <see cref="SendReceipt"/> command — exactly one running instance processes each one, because
/// Program.cs delivers it with <c>bus.SendAsync</c>.
/// </summary>
public sealed class SendReceiptHandler(ILogger<SendReceiptHandler> logger) : IMessageHandler<SendReceipt>
{
    public Task HandleAsync(IMessageContext<SendReceipt> context, CancellationToken cancellationToken)
    {
        logger.LogInformation("COMMAND handled: receipt for order {OrderId} sent to {Email}", context.Message.OrderId, context.Message.Email);
        return Task.CompletedTask;
    }
}
