using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.AzureStorage.Samples;
using Foundatio.Queues;
using Microsoft.Extensions.Logging;

// Azure Storage Emulator connection string
const string EmulatorConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://localhost:10000/devstoreaccount1;QueueEndpoint=http://localhost:10001/devstoreaccount1;";

// Define options
var connectionStringOption = new Option<string>("--connection-string", "-c")
{
    Description = "Azure Storage connection string (defaults to emulator)"
};

var queueOption = new Option<string>("--queue", "-q")
{
    Description = "Queue name",
    DefaultValueFactory = _ => "sample-queue"
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

var modeOption = new Option<AzureStorageQueueCompatibilityMode>("--mode")
{
    Description = "Compatibility mode (Default or Legacy)",
    DefaultValueFactory = _ => AzureStorageQueueCompatibilityMode.Default
};

var countOption = new Option<int>("--count")
{
    Description = "Number of messages to send",
    DefaultValueFactory = _ => 1
};

// Create root command
var rootCommand = new RootCommand("Azure Storage Queue Enqueue Sample");
rootCommand.Options.Add(connectionStringOption);
rootCommand.Options.Add(queueOption);
rootCommand.Options.Add(messageOption);
rootCommand.Options.Add(correlationIdOption);
rootCommand.Options.Add(propertiesOption);
rootCommand.Options.Add(modeOption);
rootCommand.Options.Add(countOption);

// Set handler
rootCommand.SetAction(async parseResult =>
{
    var connectionString = parseResult.GetValue(connectionStringOption) ??
                          Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING") ??
                          EmulatorConnectionString;

    var queueName = parseResult.GetValue(queueOption);
    var message = parseResult.GetValue(messageOption);
    var correlationId = parseResult.GetValue(correlationIdOption);
    var properties = parseResult.GetValue(propertiesOption);
    var mode = parseResult.GetValue(modeOption);
    var count = parseResult.GetValue(countOption);

    Console.WriteLine($"Using connection: {(connectionString == EmulatorConnectionString ? "Azure Storage Emulator" : "Custom connection string")}");
    Console.WriteLine($"Mode: {mode}");
    Console.WriteLine();

    await EnqueueMessages(connectionString, queueName, message, correlationId, properties, mode, count);
    return 0;
});

// Parse and invoke
return await rootCommand.Parse(args).InvokeAsync();

static async Task EnqueueMessages(string connectionString, string queueName, string message, string correlationId, string[] properties, AzureStorageQueueCompatibilityMode mode, int count)
{
    using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger("Enqueue");

    logger.LogInformation("Creating queue with mode: {Mode}", mode);

    using var queue = new AzureStorageQueue<SampleMessage>(options => options
        .ConnectionString(connectionString)
        .Name(queueName)
        .CompatibilityMode(mode)
        .LoggerFactory(loggerFactory));

    var queueProperties = new Dictionary<string, string>();
    if (properties != null)
    {
        foreach (var prop in properties)
        {
            var parts = prop.Split('=', 2);
            if (parts.Length == 2)
            {
                queueProperties[parts[0]] = parts[1];
            }
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

        var messageId = await queue.EnqueueAsync(sampleMessage, entryOptions);

        logger.LogInformation("Enqueued message {MessageId}: '{Message}' with CorrelationId: '{CorrelationId}' Properties: [{Properties}]",
            messageId, sampleMessage.Message, correlationId ?? "<none>",
            string.Join(", ", queueProperties.Select(p => $"{p.Key}={p.Value}")));
    }

    logger.LogInformation("Successfully enqueued {Count} message(s)", count);
}
