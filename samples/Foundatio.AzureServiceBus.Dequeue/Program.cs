using System;
using System.CommandLine;
using System.Linq;
using System.Threading;
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

var countOption = new Option<int>("--count")
{
    Description = "Number of messages to process (0 = infinite)",
    DefaultValueFactory = _ => 1
};

var rootCommand = new RootCommand("Azure Service Bus Queue Dequeue Sample");
rootCommand.Options.Add(connectionStringOption);
rootCommand.Options.Add(queueOption);
rootCommand.Options.Add(countOption);

rootCommand.SetAction(async parseResult =>
{
    string connectionString = parseResult.GetValue(connectionStringOption) ??
                              Environment.GetEnvironmentVariable("AZURE_SERVICEBUS_CONNECTION_STRING") ??
                              EmulatorConnectionString;

    string queueName = parseResult.GetValue(queueOption);
    int count = parseResult.GetValue(countOption);

    Console.WriteLine($"Using connection: {(connectionString == EmulatorConnectionString ? "Azure Service Bus Emulator" : "Custom connection string")}");
    Console.WriteLine($"Queue: {queueName}");
    Console.WriteLine($"To process: {(count == 0 ? "infinite messages" : $"{count} message(s)")}");
    Console.WriteLine();
    Console.WriteLine("Press Ctrl+C to stop...");
    Console.WriteLine();

    await DequeueMessages(connectionString, queueName, count);
    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();

static async Task DequeueMessages(string connectionString, string queueName, int count)
{
    using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger("Dequeue");
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

    logger.LogInformation("Creating queue: {QueueName}", queueName);

    using var queue = new AzureServiceBusQueue<SampleMessage>(options => options
        .ConnectionString(connectionString)
        .Name(queueName)
        .LoggerFactory(loggerFactory));

    int processed = 0;
    bool infinite = count == 0;

    logger.LogInformation("Waiting for messages... (Press Ctrl+C to stop)");

    try
    {
        while (!cts.Token.IsCancellationRequested && (infinite || processed < count))
        {
            var entry = await queue.DequeueAsync(cts.Token);

            if (entry == null)
            {
                if (!infinite && processed >= count)
                    break;

                continue;
            }

            try
            {
                processed++;

                logger.LogInformation("Dequeued message {MessageId}: '{Message}' from '{Source}' at {Timestamp}",
                    entry.Id, entry.Value.Message, entry.Value.Source, entry.Value.Timestamp);

                logger.LogInformation("  CorrelationId: '{CorrelationId}'", entry.CorrelationId ?? "<none>");

                if (entry.Properties is { Count: > 0 })
                {
                    logger.LogInformation("  Properties: [{Properties}]",
                        String.Join(", ", entry.Properties.Select(p => $"{p.Key}={p.Value}")));
                }
                else
                {
                    logger.LogInformation("  Properties: <none>");
                }

                await Task.Delay(100, cts.Token);
                await entry.CompleteAsync();
                logger.LogInformation("  Completed message {MessageId}", entry.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing message {MessageId}", entry.Id);
                await entry.AbandonAsync();
            }
        }
    }
    catch (OperationCanceledException ex)
    {
        logger.LogInformation(ex, "Operation was cancelled");
    }

    logger.LogInformation("Processed {ProcessedCount} message(s)", processed);
}
