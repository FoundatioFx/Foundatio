using System;
using Amazon.SQS.Model;

namespace Foundatio.Queues {
    public static class MessageExtensions {
        public static int ApproximateReceiveCount(this Message message) {
            if (message == null || message.Attributes == null)
                return 0;

            if (!message.Attributes.TryGetValue("ApproximateReceiveCount", out string v))
                return 0;

            int.TryParse(v, out int value);
            return value;
        }


        public static DateTime SentTimestamp(this Message message) {
            // message was sent to the queue (epoch time in milliseconds)
            if (message == null || message.Attributes == null)
                return DateTime.MinValue;

            if (!message.Attributes.TryGetValue("SentTimestamp", out string v))
                return DateTime.MinValue;

            if (!long.TryParse(v, out long value))
                return DateTime.MinValue;

            return DateTimeOffset.FromUnixTimeMilliseconds(value).DateTime;
        }

    }

}
