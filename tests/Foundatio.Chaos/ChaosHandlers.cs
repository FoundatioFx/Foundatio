using System.Diagnostics;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Chaos;

public sealed class ChaosFailureException(string message) : Exception(message);

/// <summary>Records every handler run in the ledger: <c>B</c> when it starts and <c>F</c> with its outcome.</summary>
public sealed class ChaosHandlers(Ledger ledger, ChaosSettings settings)
{
    private static int _invocations;
    private readonly int _minWorkMs = settings.GetInt("work-min-ms", 1);
    private readonly int _maxWorkMs = settings.GetInt("work-max-ms", 10);

    public static int Running;

    public async Task HandleAsync(string stream, string id, FailMode fail, int slowMs, string deliveryId, IHandlerContext context, CancellationToken cancellationToken)
    {
        int invocation = Interlocked.Increment(ref _invocations);
        Interlocked.Increment(ref Running);
        ledger.Write("B", stream, id, context.Attempt, invocation, deliveryId);
        string outcome = "ok";
        try
        {
            int workMs = slowMs > 0 ? slowMs : Random.Shared.Next(_minWorkMs, _maxWorkMs + 1);
            await Task.Delay(workMs, cancellationToken);

            if (fail == FailMode.Always || (fail == FailMode.FirstAttempt && context.Attempt == 1))
            {
                outcome = "throw";
                throw new ChaosFailureException($"{id} fails on attempt {context.Attempt} ({fail})");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancel";
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref Running);
            ledger.Write("F", stream, id, outcome, invocation);
        }
    }
}

public sealed class ChaosJobHandler(ChaosHandlers handlers) : IJobHandler<ChaosJob>
{
    public Task HandleAsync(ChaosJob arguments, IJobContext context, CancellationToken cancellationToken)
        => handlers.HandleAsync(Streams.Job, arguments.Id, arguments.Fail, arguments.SlowMs, context.JobId, context, cancellationToken);
}

/// <summary>Writes memory, handle, thread and processor counts to the ledger every few seconds.</summary>
public sealed class MetricsSampler(Ledger ledger, ChaosSettings settings, IServiceProvider services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(settings.GetInt("metrics-seconds", 5));
        var status = services.GetService<MessagingStatus>();
        using var process = Process.GetCurrentProcess();
        while (!stoppingToken.IsCancellationRequested)
        {
            process.Refresh();
            int fds = Directory.Exists("/proc/self/fd") ? Directory.GetFiles("/proc/self/fd").Length : process.HandleCount;
            int active = 0, buffered = 0, failing = 0;
            string? lastError = null;
            if (status is not null)
            {
                foreach (var processor in status.GetProcessors())
                {
                    active += processor.Active;
                    buffered += processor.Buffered;
                    if (processor.FailingSince is not null)
                    {
                        failing++;
                        lastError = processor.LastError;
                    }
                }
            }

            ledger.Write("M", process.WorkingSet64, GC.GetTotalMemory(false), fds, process.Threads.Count, GC.CollectionCount(2), active, buffered, ChaosHandlers.Running, failing, lastError);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
