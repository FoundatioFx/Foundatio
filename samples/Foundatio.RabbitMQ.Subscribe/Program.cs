using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.RabbitMQ;
using Microsoft.Extensions.Logging;

Option<string> connectionStringOption = new("--connection-string")
{
    Description = "RabbitMQ connection string",
    DefaultValueFactory = _ => "amqp://localhost:5672"
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

Option<int> subscriberCountOption = new("--subscriber-count")
{
    Description = "Number of concurrent subscribers",
    DefaultValueFactory = _ => 1
};

Option<string> groupIdOption = new("--group-id")
{
    Description = "Subscriber group identifier for queue naming",
    DefaultValueFactory = _ => "sample-subscriber"
};

Option<LogLevel> logLevelOption = new("--log-level")
{
    Description = "Minimum log level",
    DefaultValueFactory = _ => LogLevel.Information
};

RootCommand rootCommand = new("RabbitMQ Message Subscriber Sample")
{
    connectionStringOption,
    topicOption,
    durableOption,
    delayedOption,
    acknowledgmentStrategyOption,
    prefetchCountOption,
    deliveryLimitOption,
    subscriberCountOption,
    groupIdOption,
    logLevelOption
};

rootCommand.SetAction(parseResult =>
{
    string connectionString = parseResult.GetValue(connectionStringOption);
    string topic = parseResult.GetValue(topicOption);
    bool durable = parseResult.GetValue(durableOption);
    bool delayed = parseResult.GetValue(delayedOption);
    string acknowledgmentStrategy = parseResult.GetValue(acknowledgmentStrategyOption);
    ushort prefetchCount = parseResult.GetValue(prefetchCountOption);
    long deliveryLimit = parseResult.GetValue(deliveryLimitOption);
    int subscriberCount = parseResult.GetValue(subscriberCountOption);
    string groupId = parseResult.GetValue(groupIdOption);
    LogLevel logLevel = parseResult.GetValue(logLevelOption);

    RunSubscriber(
        connectionString, topic, durable, delayed, acknowledgmentStrategy,
        prefetchCount, deliveryLimit, subscriberCount, groupId, logLevel);
});

return await rootCommand.Parse(args).InvokeAsync();

static void RunSubscriber(
    string connectionString,
    string topic,
    bool durable,
    bool delayed,
    string acknowledgmentStrategy,
    ushort prefetchCount,
    long deliveryLimit,
    int subscriberCount,
    string groupId,
    LogLevel logLevel)
{
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddConsole().SetMinimumLevel(logLevel);
    });
    ILogger logger = loggerFactory.CreateLogger("Subscriber");

    if (delayed)
    {
        Uri uri = new(connectionString);
        connectionString = new UriBuilder(uri) { Port = 5673 }.Uri.ToString();
    }

    AcknowledgementStrategy ackStrategy = String.Equals("automatic", acknowledgmentStrategy, StringComparison.OrdinalIgnoreCase)
        ? AcknowledgementStrategy.Automatic
        : AcknowledgementStrategy.FireAndForget;

    logger.LogInformation("Configuration:");
    logger.LogInformation("  Connection String: {ConnectionString}", connectionString);
    logger.LogInformation("  Topic: {Topic}", topic);
    logger.LogInformation("  Durable: {Durable}", durable);
    logger.LogInformation("  Delayed Exchange: {Delayed}", delayed);
    logger.LogInformation("  Acknowledgment Strategy: {AckStrategy}", ackStrategy);
    logger.LogInformation("  Prefetch Count: {PrefetchCount}", prefetchCount);
    logger.LogInformation("  Delivery Limit: {DeliveryLimit}", deliveryLimit);
    logger.LogInformation("  Subscriber Count: {SubscriberCount}", subscriberCount);
    logger.LogInformation("  Group ID: {GroupId}", groupId);

    List<IMessageBus> messageBuses = new();
    List<Task> subscriptions = new();

    try
    {
        for (int i = 0; i < subscriberCount; i++)
        {
            int subscriberId = i + 1;
            string queueName = durable
                ? $"{groupId}-{nameof(MyMessage).ToLower()}"
                : $"{groupId}-{nameof(MyMessage).ToLower()}-{Guid.NewGuid():N}";

            RabbitMQMessageBusOptions options = new()
            {
                ConnectionString = connectionString,
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

            RabbitMQMessageBus messageBus = new(options);
            messageBuses.Add(messageBus);

            int localSubscriberId = subscriberId;
            var subscription = messageBus.SubscribeAsync<MyMessage>(msg =>
            {
                TimeSpan latency = DateTimeOffset.UtcNow - msg.Timestamp;
                int payloadSize = msg.Payload?.Length ?? 0;
                logger.LogInformation(
                    "Subscriber {SubscriberId} received: {MessageId} | Content: {Content} | Latency: {Latency}ms | Payload: {PayloadSize} bytes",
                    localSubscriberId, msg.Id, msg.Hey, latency.TotalMilliseconds, payloadSize);
            });

            subscriptions.Add(subscription);
            logger.LogInformation("Subscriber {SubscriberId} started with queue: {QueueName}", subscriberId, queueName);
        }

        Task.WhenAll(subscriptions).GetAwaiter().GetResult();

        logger.LogInformation("Waiting to receive messages, press enter to quit...");
        Console.ReadLine();
    }
    finally
    {
        logger.LogInformation("Shutting down subscribers...");
        foreach (IMessageBus messageBus in messageBuses)
            messageBus.Dispose();

        logger.LogInformation("All subscribers stopped.");
    }
}
