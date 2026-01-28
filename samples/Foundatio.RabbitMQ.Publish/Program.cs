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
    Description = "Target message size in bytes (pads Notes field to reach size)",
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

RootCommand rootCommand = new("RabbitMQ Order Publisher Sample")
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
        ? $"{processName}-{nameof(OrderEvent).ToLower()}"
        : $"{processName}-{nameof(OrderEvent).ToLower()}-{Guid.NewGuid():N}";

    // Parse hosts into a list if provided
    List<string> hostsList = [];
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

    logger.LogInformation("Config: ConnectionString={ConnectionString}, Topic={Topic}, Durable={Durable}, AckStrategy={AckStrategy}, MessageSize={MessageSize}, Interval={Interval}ms, DelaySeconds={DelaySeconds}",
        connectionString, topic, durable, ackStrategy, messageSize, interval, delaySeconds);
    if (hostsList.Count > 0)
        logger.LogInformation("Hosts: {Hosts}", String.Join(", ", hostsList));

    using RabbitMQMessageBus messageBus = new(options);

    if (interval > 0)
    {
        // Auto-send mode
        logger.LogInformation("Auto-send mode enabled. Press Ctrl+C to stop.");
        int orderCount = 0;

        try
        {
            while (true)
            {
                ++orderCount;
                var order = OrderEvent.Create(orderCount, messageSize);

                TimeSpan? delay = delaySeconds > 0 ? TimeSpan.FromSeconds(delaySeconds) : null;
                if (delay.HasValue)
                {
                    await messageBus.PublishAsync(order, delay.Value);
                    logger.LogInformation("Order #{Seq} | {OrderId} | Customer: {Customer} | ${Amount} | {Delay}s delay",
                        orderCount, order.OrderId, order.CustomerId, order.Amount, delaySeconds);
                }
                else
                {
                    await messageBus.PublishAsync(order);
                    logger.LogInformation("Order #{Seq} | {OrderId} | Customer: {Customer} | ${Amount}",
                        orderCount, order.OrderId, order.CustomerId, order.Amount);
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
            logger.LogInformation("Exiting. Total orders sent: {Count}", orderCount);
        }
    }
    else
    {
        // Manual mode
        logger.LogInformation("Press enter to send an order (empty line to exit):");

        int orderCount = 0;
        do
        {
            string input = Console.ReadLine();
            if (String.IsNullOrEmpty(input))
                break;

            ++orderCount;
            var order = OrderEvent.Create(orderCount, messageSize);

            TimeSpan? delay = delaySeconds > 0 ? TimeSpan.FromSeconds(delaySeconds) : null;
            if (delay.HasValue)
            {
                await messageBus.PublishAsync(order, delay.Value);
                logger.LogInformation("Order #{Seq} | {OrderId} | Customer: {Customer} | ${Amount} | {Delay}s delay",
                    orderCount, order.OrderId, order.CustomerId, order.Amount, delaySeconds);
            }
            else
            {
                await messageBus.PublishAsync(order);
                logger.LogInformation("Order #{Seq} | {OrderId} | Customer: {Customer} | ${Amount}",
                    orderCount, order.OrderId, order.CustomerId, order.Amount);
            }
        } while (true);

        logger.LogInformation("Exiting. Total orders sent: {Count}", orderCount);
    }
}
