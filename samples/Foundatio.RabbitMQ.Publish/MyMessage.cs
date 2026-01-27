using System;

namespace Foundatio.RabbitMQ;

public class MyMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Hey { get; set; }
    public string Payload { get; set; }

    public static MyMessage Create(string message, int targetSizeBytes = 0)
    {
        MyMessage msg = new() { Hey = message };

        if (targetSizeBytes > 0)
        {
            int currentSize = (msg.Id?.Length ?? 0) + (msg.Hey?.Length ?? 0) + 50;
            int paddingNeeded = Math.Max(0, targetSizeBytes - currentSize);
            if (paddingNeeded > 0)
                msg.Payload = new string('X', paddingNeeded);
        }

        return msg;
    }
}
