using System;
using System.CommandLine;
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

var eventTypeOption = new Option<string>("--event-type", "-e")
{
    Description = "Event type",
    DefaultValueFactory = _ => "SampleEvent"
};

var dataOption = new Option<string>("--data", "-d")
{
    Description = "Event data",
    DefaultValueFactory = _ => "Hello World"
};

var correlationIdOption = new Option<string>("--correlation-id")
{
    Description = "Correlation ID for the message"
};

var countOption = new Option<int>("--count")
{
    Description = "Number of messages to publish",
    DefaultValueFactory = _ => 1
};

var rootCommand = new RootCommand("Azure Service Bus Message Bus Publish Sample");
rootCommand.Options.Add(connectionStringOption);
rootCommand.Options.Add(topicOption);
rootCommand.Options.Add(eventTypeOption);
rootCommand.Options.Add(dataOption);
rootCommand.Options.Add(correlationIdOption);
rootCommand.Options.Add(countOption);

rootCommand.SetAction(async parseResult =>
{
    string connectionString = parseResult.GetValue(connectionStringOption) ??
                              Environment.GetEnvironmentVariable("AZURE_SERVICEBUS_CONNECTION_STRING") ??
                              EmulatorConnectionString;

    string topic = parseResult.GetValue(topicOption);
    string eventType = parseResult.GetValue(eventTypeOption);
    string data = parseResult.GetValue(dataOption);
    string correlationId = parseResult.GetValue(correlationIdOption);
    int count = parseResult.GetValue(countOption);

    Console.WriteLine($"Using connection: {(connectionString == EmulatorConnectionString ? "Azure Service Bus Emulator" : "Custom connection string")}");
    Console.WriteLine();

    await PublishMessages(connectionString, topic, eventType, data, correlationId, count);
    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();

static async Task PublishMessages(string connectionString, string topic, string eventType, string data, string correlationId, int count)
{
    using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger("Publish");

    logger.LogInformation("Creating message bus with topic: {Topic}", topic);

    using var messageBus = new AzureServiceBusMessageBus(options => options
        .ConnectionString(connectionString)
        .Topic(topic)
        .LoggerFactory(loggerFactory));

    for (int i = 0; i < count; i++)
    {
        var sampleEvent = new SampleEvent
        {
            EventType = eventType,
            Data = count > 1 ? $"{data} #{i + 1}" : data,
            Source = "Publish Sample"
        };

        var messageOptions = new MessageOptions
        {
            CorrelationId = correlationId
        };

        await messageBus.PublishAsync(sampleEvent, messageOptions);

        logger.LogInformation("Published event '{EventType}': '{Data}' with CorrelationId: '{CorrelationId}'",
            sampleEvent.EventType, sampleEvent.Data, correlationId ?? "<none>");
    }

    logger.LogInformation("Successfully published {Count} event(s)", count);
}
