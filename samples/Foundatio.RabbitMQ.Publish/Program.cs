using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.RabbitMQ;
using Microsoft.Extensions.Logging;

Option<string> connectionStringOption = new("--connection-string")
{
    Description = "RabbitMQ connection string (provides credentials and vhost)",
    DefaultValueFactory = _ => "amqp://localhost:5672"
};

Option<string> hostsOption = new("--hosts")
{
    Description = "Comma-separated list of hosts for failover (e.g., localhost:5672,localhost:5673,localhost:5674)"
};

Option<string> topicOption = new("--topic")
{
    Description = "Message topic/exchange name",
    DefaultValueFactory = _ => "sample-topic"
};

Option<bool> durableOption = new("--durable")
{
    Description = "Use durable queues that survive broker restarts"
};

Option<bool> delayedOption = new("--delayed")
{
    Description = "Use delayed exchange (connects to port 5673)"
};

Option<string> acknowledgmentStrategyOption = new("--acknowledgment-strategy")
{
    Description = "Acknowledgment strategy: fireandforget or automatic",
    DefaultValueFactory = _ => "fireandforget"
};

Option<int> messageSizeOption = new("--message-size")
{
    Description = "Target message size in bytes (for testing broker limits)",
    DefaultValueFactory = _ => 0
};

Option<ushort> prefetchCountOption = new("--prefetch-count")
{
    Description = "Consumer prefetch count",
    DefaultValueFactory = _ => 10
};

Option<long> deliveryLimitOption = new("--delivery-limit")
{
    Description = "Maximum delivery attempts before discarding",
    DefaultValueFactory = _ => 2
};

Option<int> delaySecondsOption = new("--delay-seconds")
{
    Description = "Delay in seconds before message delivery",
    DefaultValueFactory = _ => 0
};

Option<int> intervalOption = new("--interval")
{
    Description = "Auto-send interval in milliseconds (0 = manual mode, prompts for input)",
    DefaultValueFactory = _ => 0
};

Option<LogLevel> logLevelOption = new("--log-level")
{
    Description = "Minimum log level",
    DefaultValueFactory = _ => LogLevel.Information
};

RootCommand rootCommand = new("RabbitMQ Message Publisher Sample")
{
    connectionStringOption,
    hostsOption,
    topicOption,
    durableOption,
    delayedOption,
    acknowledgmentStrategyOption,
    messageSizeOption,
    prefetchCountOption,
    deliveryLimitOption,
    delaySecondsOption,
    intervalOption,
    logLevelOption
};

rootCommand.SetAction(parseResult =>
{
    string connectionString = parseResult.GetValue(connectionStringOption);
    string hosts = parseResult.GetValue(hostsOption);
    string topic = parseResult.GetValue(topicOption);
    bool durable = parseResult.GetValue(durableOption);
    bool delayed = parseResult.GetValue(delayedOption);
    string acknowledgmentStrategy = parseResult.GetValue(acknowledgmentStrategyOption);
    int messageSize = parseResult.GetValue(messageSizeOption);
    ushort prefetchCount = parseResult.GetValue(prefetchCountOption);
    long deliveryLimit = parseResult.GetValue(deliveryLimitOption);
    int delaySeconds = parseResult.GetValue(delaySecondsOption);
    int interval = parseResult.GetValue(intervalOption);
    LogLevel logLevel = parseResult.GetValue(logLevelOption);

    return RunPublisher(
        connectionString, hosts, topic, durable, delayed, acknowledgmentStrategy,
        messageSize, prefetchCount, deliveryLimit, delaySeconds, interval, logLevel);
});

return await rootCommand.Parse(args).InvokeAsync();

static async Task RunPublisher(
    string connectionString,
    string hosts,
    string topic,
    bool durable,
    bool delayed,
    string acknowledgmentStrategy,
    int messageSize,
    ushort prefetchCount,
    long deliveryLimit,
    int delaySeconds,
    int interval,
    LogLevel logLevel)
{
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddConsole().SetMinimumLevel(logLevel);
    });
    var logger = loggerFactory.CreateLogger("Publisher");

    if (delayed)
    {
        Uri uri = new(connectionString);
        connectionString = new UriBuilder(uri) { Port = 5673 }.Uri.ToString();
    }

    var ackStrategy = String.Equals("automatic", acknowledgmentStrategy, StringComparison.OrdinalIgnoreCase)
        ? AcknowledgementStrategy.Automatic
        : AcknowledgementStrategy.FireAndForget;

    string processName = "sample-publisher";
    string queueName = durable
        ? $"{processName}-{nameof(MyMessage).ToLower()}"
        : $"{processName}-{nameof(MyMessage).ToLower()}-{Guid.NewGuid():N}";

    // Parse hosts into a list if provided
    List<string> hostsList = new();
    if (!String.IsNullOrEmpty(hosts))
    {
        hostsList.AddRange(hosts.Split(',', StringSplitOptions.RemoveEmptyEntries)
                               .Select(h => h.Trim()));
    }

    RabbitMQMessageBusOptions options = new()
    {
        ConnectionString = connectionString,
        Hosts = hostsList,
        Topic = topic,
        AcknowledgementStrategy = ackStrategy,
        IsDurable = durable,
        SubscriptionQueueName = queueName,
        IsSubscriptionQueueExclusive = !durable,
        SubscriptionQueueAutoDelete = !durable,
        PrefetchCount = prefetchCount,
        DeliveryLimit = deliveryLimit,
        LoggerFactory = loggerFactory
    };

    logger.LogInformation("Configuration:");
    logger.LogInformation("  Connection String: {ConnectionString}", connectionString);
    if (hostsList.Count > 0)
        logger.LogInformation("  Hosts: {Hosts}", String.Join(", ", hostsList));
    logger.LogInformation("  Topic: {Topic}", topic);
    logger.LogInformation("  Durable: {Durable}", durable);
    logger.LogInformation("  Delayed Exchange: {Delayed}", delayed);
    logger.LogInformation("  Acknowledgment Strategy: {AckStrategy}", ackStrategy);
    logger.LogInformation("  Message Size: {MessageSize} bytes", messageSize);
    logger.LogInformation("  Prefetch Count: {PrefetchCount}", prefetchCount);
    logger.LogInformation("  Delivery Limit: {DeliveryLimit}", deliveryLimit);
    logger.LogInformation("  Delay Seconds: {DelaySeconds}", delaySeconds);
    logger.LogInformation("  Interval: {Interval}ms", interval);

    using RabbitMQMessageBus messageBus = new(options);

    if (interval > 0)
    {
        // Auto-send mode
        logger.LogInformation("Auto-send mode enabled. Press Ctrl+C to stop.");
        int messageCount = 0;

        try
        {
            while (true)
            {
                var body = MyMessage.Create($"Message #{++messageCount} at {DateTimeOffset.UtcNow:O}", messageSize);

                TimeSpan? delay = delaySeconds > 0 ? TimeSpan.FromSeconds(delaySeconds) : null;
                if (delay.HasValue)
                {
                    await messageBus.PublishAsync(body, delay.Value);
                    logger.LogInformation("Message {Count} sent with {Delay}s delay: {MessageId} at {Time}",
                        messageCount, delaySeconds, body.Id, DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff"));
                }
                else
                {
                    await messageBus.PublishAsync(body);
                    logger.LogInformation("Message {Count} sent: {MessageId} at {Time}",
                        messageCount, body.Id, DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff"));
                }

                await Task.Delay(interval);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error in auto-send loop");
        }
        finally
        {
            logger.LogInformation("Exiting. Total messages sent: {Count}", messageCount);
        }
    }
    else
    {
        // Manual mode
        logger.LogInformation("Enter the message and press enter to send (empty to exit):");

        int messageCount = 0;
        do
        {
            string message = Console.ReadLine();
            if (String.IsNullOrEmpty(message))
                break;

            var body = MyMessage.Create(message, messageSize);

            TimeSpan? delay = delaySeconds > 0 ? TimeSpan.FromSeconds(delaySeconds) : null;
            if (delay.HasValue)
            {
                await messageBus.PublishAsync(body, delay.Value);
                logger.LogInformation("Message {Count} sent with {Delay}s delay: {MessageId} ({Size} bytes)",
                    ++messageCount, delaySeconds, body.Id, messageSize > 0 ? messageSize : message.Length);
            }
            else
            {
                await messageBus.PublishAsync(body);
                logger.LogInformation("Message {Count} sent: {MessageId} ({Size} bytes)",
                    ++messageCount, body.Id, messageSize > 0 ? messageSize : message.Length);
            }

            logger.LogInformation("Enter new message or press enter to exit:");
        } while (true);

        logger.LogInformation("Exiting. Total messages sent: {Count}", messageCount);
    }
}
