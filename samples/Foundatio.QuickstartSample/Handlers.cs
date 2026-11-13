using Foundatio.Messaging;

namespace Foundatio.QuickstartSample;

/// <summary>Reacts to an event by sending a command. Handlers are resolved from DI in a new scope per message.</summary>
public sealed class OrderPlacedHandler(IMessageClient messages, ILogger<OrderPlacedHandler> logger) : IMessageHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced order, IMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("Order {OrderId} placed for {Total:C}", order.OrderId, order.Total);

        // The receipt inherits this message's correlation id automatically.
        await messages.SendAsync(new SendReceipt(order.OrderId, order.Email), cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Handles a command. Throwing retries it with backoff; after the last attempt it is dead-lettered. Reporting progress
/// shows the delivery in the monitor, however quickly it finishes.
/// </summary>
public sealed class SendReceiptHandler(SampleActivity activity, ILogger<SendReceiptHandler> logger) : IMessageHandler<SendReceipt>
{
    public async Task HandleAsync(SendReceipt receipt, IMessageContext context, CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync(50, $"Rendering the receipt for order {receipt.OrderId}", cancellationToken);
        logger.LogInformation("Receipt for order {OrderId} sent to {Email} (attempt {Attempt}, correlation {CorrelationId})",
            receipt.OrderId, receipt.Email, context.Attempt, context.CorrelationId);
        await context.ReportProgressAsync(100, $"Sent to {receipt.Email}", cancellationToken);
        activity.ReceiptSent.TrySetResult();
    }
}
