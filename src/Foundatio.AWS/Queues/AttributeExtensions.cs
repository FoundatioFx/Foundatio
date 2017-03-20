using System;
using System.Collections.Generic;
using System.Linq;
using Amazon.SQS.Model;
using ThirdParty.Json.LitJson;

namespace Foundatio.Queues {

    public static class AttributeExtensions {
        public static int ApproximateReceiveCount(this IDictionary<string, string> attributes) {
            if (attributes == null)
                return 0;

            if (!attributes.TryGetValue("ApproximateReceiveCount", out string v))
                return 0;

            int.TryParse(v, out int value);
            return value;
        }


        public static DateTime SentTimestamp(this IDictionary<string, string> attributes) {
            // message was sent to the queue (epoch time in milliseconds)
            if (attributes == null)
                return DateTime.MinValue;

            if (!attributes.TryGetValue("SentTimestamp", out string v))
                return DateTime.MinValue;

            if (!long.TryParse(v, out long value))
                return DateTime.MinValue;

            return DateTimeOffset.FromUnixTimeMilliseconds(value).DateTime;
        }

        public static string RedrivePolicy(this IDictionary<string, string> attributes) {
            if (attributes == null)
                return null;

            if (!attributes.TryGetValue("RedrivePolicy", out string v))
                return null;

            return v;
        }


        public static string DeadLetterQueue(this IDictionary<string, string> attributes) {
            if (attributes == null)
                return null;

            if (!attributes.TryGetValue("RedrivePolicy", out string v))
                return null;

            if (string.IsNullOrEmpty(v))
                return null;

            var redrivePolicy = JsonMapper.ToObject(v);


            var arn =  redrivePolicy["deadLetterTargetArn"]?.ToString();
            if (string.IsNullOrEmpty(arn))
                return null;

            var parts = arn.Split(':');
            return parts.LastOrDefault();
        }

    }

}
