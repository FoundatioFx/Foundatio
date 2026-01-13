using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.AzureServiceBus.Samples;
using Foundatio.Queues;
using Microsoft.Extensions.Logging;

const string EmulatorConnectionString = "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

var connectionStringOption = new Option<string>("--connection-string", "-c")
{
    Description = "Azure Service Bus connection string (defaults to emulator)"
};

var queueOption = new Option<string>("--queue", "-q")
{
    Description = "Queue name",
    DefaultValueFactory = _ => "foundatio-test-queue"
};

var messageOption = new Option<string>("--message", "-m")
{
    Description = "Message to send",
    DefaultValueFactory = _ => "Hello World"
};

var correlationIdOption = new Option<string>("--correlation-id")
{
    Description = "Correlation ID for the message"
};

var propertiesOption = new Option<string[]>("--property")
{
    Description = "Custom properties in key=value format",
    DefaultValueFactory = _ => Array.Empty<string>()
};

var countOption = new Option<int>("--count")
{
    Description = "Number of messages to send",
    DefaultValueFactory = _ => 1
};

var rootCommand = new RootCommand("Azure Service Bus Queue Enqueue Sample");
rootCommand.Options.Add(connectionStringOption);
rootCommand.Options.Add(queueOption);
rootCommand.Options.Add(messageOption);
rootCommand.Options.Add(correlationIdOption);
rootCommand.Options.Add(propertiesOption);
rootCommand.Options.Add(countOption);

rootCommand.SetAction(async parseResult =>
{
    string connectionString = parseResult.GetValue(connectionStringOption) ??
                              Environment.GetEnvironmentVariable("AZURE_SERVICEBUS_CONNECTION_STRING") ??
                              EmulatorConnectionString;

    string queueName = parseResult.GetValue(queueOption);
    string message = parseResult.GetValue(messageOption);
    string correlationId = parseResult.GetValue(correlationIdOption);
    string[] properties = parseResult.GetValue(propertiesOption);
    int count = parseResult.GetValue(countOption);

    Console.WriteLine($"Using connection: {(connectionString == EmulatorConnectionString ? "Azure Service Bus Emulator" : "Custom connection string")}");
    Console.WriteLine();

    await EnqueueMessages(connectionString, queueName, message, correlationId, properties, count);
    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();

static async Task EnqueueMessages(string connectionString, string queueName, string message, string correlationId, string[] properties, int count)
{
    using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger("Enqueue");

    logger.LogInformation("Creating queue: {QueueName}", queueName);

    using var queue = new AzureServiceBusQueue<SampleMessage>(options => options
        .ConnectionString(connectionString)
        .Name(queueName)
        .LoggerFactory(loggerFactory));

    var queueProperties = new Dictionary<string, string>();
    if (properties != null)
    {
        foreach (string prop in properties)
        {
            string[] parts = prop.Split('=', 2);
            if (parts.Length == 2)
                queueProperties[parts[0]] = parts[1];
        }
    }

    for (int i = 0; i < count; i++)
    {
        var sampleMessage = new SampleMessage
        {
            Message = count > 1 ? $"{message} #{i + 1}" : message,
            Source = "Enqueue Sample"
        };

        var entryOptions = new QueueEntryOptions
        {
            CorrelationId = correlationId,
            Properties = queueProperties.Count > 0 ? queueProperties : null
        };

        string messageId = await queue.EnqueueAsync(sampleMessage, entryOptions);

        logger.LogInformation("Enqueued message {MessageId}: '{Message}' with CorrelationId: '{CorrelationId}' Properties: [{Properties}]",
            messageId, sampleMessage.Message, correlationId ?? "<none>",
            String.Join(", ", queueProperties.Select(p => $"{p.Key}={p.Value}")));
    }

    logger.LogInformation("Successfully enqueued {Count} message(s)", count);
}
