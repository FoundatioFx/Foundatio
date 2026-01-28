using System;

namespace Foundatio.RabbitMQ;

public record OrderEvent
{
    public string OrderId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int SequenceNumber { get; init; }
    public string CustomerId { get; init; }
    public decimal Amount { get; init; }
    public string Status { get; init; }
    public string Notes { get; init; }

    public static OrderEvent Create(int sequenceNumber, int targetSizeBytes = 0)
    {
        var order = new OrderEvent
        {
            SequenceNumber = sequenceNumber,
            CustomerId = $"CUST-{Random.Shared.Next(1000, 9999)}",
            Amount = Math.Round((decimal)(Random.Shared.NextDouble() * 500 + 10), 2),
            Status = "Created"
        };

        if (targetSizeBytes > 0)
        {
            int currentSize = (order.OrderId?.Length ?? 0) + (order.CustomerId?.Length ?? 0) + 100;
            int paddingNeeded = Math.Max(0, targetSizeBytes - currentSize);
            if (paddingNeeded > 0)
                order = order with { Notes = new string('X', paddingNeeded) };
        }

        return order;
    }
}
