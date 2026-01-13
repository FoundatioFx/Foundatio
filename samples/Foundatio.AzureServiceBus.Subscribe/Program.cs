using System;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AzureServiceBus.Samples;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;

const string EmulatorConnectionString = "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

var connectionStringOption = new Option<string>("--connection-string", "-c")
{
    Description = "Azure Service Bus connection string (defaults to emulator)"
};

var topicOption = new Option<string>("--topic", "-t")
{
    Description = "Topic name",
    DefaultValueFactory = _ => "test-messages"
};

var subscriptionOption = new Option<string>("--subscription", "-s")
{
    Description = "Subscription name",
    DefaultValueFactory = _ => "test-subscription"
};

var rootCommand = new RootCommand("Azure Service Bus Message Bus Subscribe Sample");
rootCommand.Options.Add(connectionStringOption);
rootCommand.Options.Add(topicOption);
rootCommand.Options.Add(subscriptionOption);

rootCommand.SetAction(async parseResult =>
{
    string connectionString = parseResult.GetValue(connectionStringOption) ??
                              Environment.GetEnvironmentVariable("AZURE_SERVICEBUS_CONNECTION_STRING") ??
                              EmulatorConnectionString;

    string topic = parseResult.GetValue(topicOption);
    string subscription = parseResult.GetValue(subscriptionOption);

    Console.WriteLine($"Using connection: {(connectionString == EmulatorConnectionString ? "Azure Service Bus Emulator" : "Custom connection string")}");
    Console.WriteLine($"Topic: {topic}");
    Console.WriteLine($"Subscription: {subscription}");
    Console.WriteLine();
    Console.WriteLine("Press Ctrl+C to stop...");
    Console.WriteLine();

    await SubscribeToMessages(connectionString, topic, subscription);
    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();

static async Task SubscribeToMessages(string connectionString, string topic, string subscription)
{
    using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger("Subscribe");
    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        try
        {
            cts.Cancel();
        } catch {
        }

        logger.LogInformation("Cancellation requested...");
    };

    logger.LogInformation("Creating message bus with topic: {Topic}", topic);

    using var messageBus = new AzureServiceBusMessageBus(options =>
    {
        options.ConnectionString(connectionString)
               .Topic(topic)
               .SubscriptionName(subscription)
               .LoggerFactory(loggerFactory);

        return options;
    });

    int received = 0;

    await messageBus.SubscribeAsync<SampleEvent>(msg =>
    {
        received++;
        logger.LogInformation("Received event #{Count}: EventType='{EventType}' Data='{Data}' Source='{Source}' Timestamp={Timestamp}",
            received, msg.EventType, msg.Data, msg.Source, msg.Timestamp);
    }, cts.Token);

    logger.LogInformation("Subscribed to SampleEvent messages. Waiting for events...");

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("Subscription stopped");
    }

    logger.LogInformation("Received {Count} event(s)", received);
}
