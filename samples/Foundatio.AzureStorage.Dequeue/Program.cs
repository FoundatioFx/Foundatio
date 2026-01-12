using System;
using System.CommandLine;
using System.Linq;
using System.Threading;
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

var modeOption = new Option<AzureStorageQueueCompatibilityMode>("--mode")
{
    Description = "Compatibility mode (Default or Legacy)",
    DefaultValueFactory = _ => AzureStorageQueueCompatibilityMode.Default
};

var countOption = new Option<int>("--count")
{
    Description = "Number of messages to process (0 = infinite)",
    DefaultValueFactory = _ => 1
};

// Create root command
var rootCommand = new RootCommand("Azure Storage Queue Dequeue Sample");
rootCommand.Options.Add(connectionStringOption);
rootCommand.Options.Add(queueOption);
rootCommand.Options.Add(modeOption);
rootCommand.Options.Add(countOption);

// Set handler
rootCommand.SetAction(async parseResult =>
{
    var connectionString = parseResult.GetValue(connectionStringOption) ??
                          Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING") ??
                          EmulatorConnectionString;

    var queueName = parseResult.GetValue(queueOption);
    var mode = parseResult.GetValue(modeOption);
    var count = parseResult.GetValue(countOption);

    Console.WriteLine($"Using connection: {(connectionString == EmulatorConnectionString ? "Azure Storage Emulator" : "Custom connection string")}");
    Console.WriteLine($"Mode: {mode}");
    Console.WriteLine($"Queue: {queueName}");
    Console.WriteLine($"To process: {(count == 0 ? "infinite messages" : $"{count} message(s)")}");
    Console.WriteLine();
    Console.WriteLine("Press Ctrl+C to stop...");
    Console.WriteLine();

    await DequeueMessages(connectionString, queueName, mode, count);
    return 0;
});

// Parse and invoke
return await rootCommand.Parse(args).InvokeAsync();

static async Task DequeueMessages(string connectionString, string queueName, AzureStorageQueueCompatibilityMode mode, int count)
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
        }
        catch
        {
            // ignored
        }

        logger.LogInformation("Cancellation requested...");
    };

    logger.LogInformation("Creating queue with mode: {Mode}", mode);

    using var queue = new AzureStorageQueue<SampleMessage>(options => options
        .ConnectionString(connectionString)
        .Name(queueName)
        .CompatibilityMode(mode)
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

                if (entry.Properties != null && entry.Properties.Count > 0)
                {
                    logger.LogInformation("  Properties: [{Properties}]",
                        string.Join(", ", entry.Properties.Select(p => $"{p.Key}={p.Value}")));
                }
                else
                {
                    logger.LogInformation("  Properties: <none>");
                }

                // Simulate processing time
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
