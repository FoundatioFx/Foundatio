using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging;

namespace Foundatio.RabbitMQ.Tests;

public class ChaosTestHelper
{
    private readonly DistributedApplication _app;
    private readonly ILogger _logger;

    public ChaosTestHelper(DistributedApplication app, ILoggerFactory? loggerFactory = null)
    {
        _app = app;
        _logger = loggerFactory?.CreateLogger<ChaosTestHelper>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ChaosTestHelper>.Instance;
    }

    public async Task FillDiskAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Setting disk_free_limit to 999GB on {Resource} to trigger alarm", resourceName);
        var containerId = await GetContainerIdAsync(resourceName, cancellationToken: cancellationToken);
        await DockerExecAsync(containerId, "rabbitmqctl set_disk_free_limit 999GB", cancellationToken);
    }

    public async Task ClearDiskAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Resetting disk_free_limit to 10MB on {Resource}", resourceName);
        var containerId = await GetContainerIdAsync(resourceName, cancellationToken: cancellationToken);
        await DockerExecAsync(containerId, "rabbitmqctl set_disk_free_limit 10MB", cancellationToken);
    }

    public async Task StopNodeAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Killing container for {Resource}", resourceName);
        var containerId = await GetContainerIdAsync(resourceName, cancellationToken: cancellationToken);
        await RunDockerCommandAsync($"kill {containerId}", cancellationToken);
    }

    public async Task StartNodeAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting container for {Resource}", resourceName);
        var containerId = await GetContainerIdAsync(resourceName, includeExited: true, cancellationToken: cancellationToken);
        await RunDockerCommandAsync($"start {containerId}", cancellationToken);
    }

    public async Task WaitForNodeReadyAsync(string resourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(timeout);
        var linkedToken = deadlineCts.Token;

        while (!linkedToken.IsCancellationRequested)
        {
            try
            {
                var containerId = await GetContainerIdAsync(resourceName, cancellationToken: linkedToken);
                var output = await DockerExecAsync(containerId, "rabbitmqctl status", linkedToken);
                if (output.Contains("pid", StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogTrace(ex, "Node {Resource} not ready yet: {Message}, retrying...", resourceName, ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), linkedToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        throw new TimeoutException($"Node '{resourceName}' did not become ready within {timeout.TotalSeconds}s");
    }

    public async Task<bool> HasDiskAlarmAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        var containerId = await GetContainerIdAsync(resourceName, cancellationToken: cancellationToken);
        var output = await DockerExecAsync(containerId, "rabbitmqctl status", cancellationToken);
        return output.Contains("disk space alarm", StringComparison.OrdinalIgnoreCase);
    }

    public async Task WaitForAlarmActiveAsync(string resourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await HasDiskAlarmAsync(resourceName, cancellationToken))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException($"Disk alarm on '{resourceName}' did not activate within {timeout.TotalSeconds}s");
    }

    public async Task WaitForAlarmClearedAsync(string resourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await HasDiskAlarmAsync(resourceName, cancellationToken))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException($"Disk alarm on '{resourceName}' did not clear within {timeout.TotalSeconds}s");
    }

    public async Task TriggerMemoryAlarmAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Setting vm_memory_high_watermark to 0.0001 on {Resource} to trigger memory alarm", resourceName);
        var containerId = await GetContainerIdAsync(resourceName, cancellationToken: cancellationToken);
        await DockerExecAsync(containerId, "rabbitmqctl set_vm_memory_high_watermark 0.0001", cancellationToken);
    }

    private const string TestResetMemoryWatermark = "0.8";

    public async Task ClearMemoryAlarmAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Resetting vm_memory_high_watermark to test default ({Watermark}) on {Resource}", TestResetMemoryWatermark, resourceName);
        var containerId = await GetContainerIdAsync(resourceName, cancellationToken: cancellationToken);
        await DockerExecAsync(containerId, $"rabbitmqctl set_vm_memory_high_watermark {TestResetMemoryWatermark}", cancellationToken);
    }

    public async Task CloseAllConnectionsAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Force-closing all connections on {Resource}", resourceName);
        var containerId = await GetContainerIdAsync(resourceName, cancellationToken: cancellationToken);
        await DockerExecAsync(containerId, "rabbitmqctl close_all_connections chaos-test", cancellationToken);
    }

    public string GetConnectionString(string resourceName)
    {
        var endpoint = _app.GetEndpoint(resourceName, "amqp");
        return $"amqp://guest:guest@{endpoint.Host}:{endpoint.Port}";
    }

    private async Task<string> GetContainerIdAsync(string resourceName, bool includeExited = false, CancellationToken cancellationToken = default)
    {
        var flags = includeExited ? "ps -aq" : "ps -q";
        var output = await RunDockerCommandAsync(
            $"{flags} --filter \"name={resourceName}\"",
            cancellationToken);

        var containerIds = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (containerIds.Length == 0)
            throw new InvalidOperationException($"Container for resource '{resourceName}' not found");

        if (containerIds.Length > 1)
            _logger.LogWarning("Multiple containers matched '{Resource}', using first: {ContainerId}", resourceName, containerIds[0]);

        return containerIds[0].Trim();
    }

    private static Task<string> DockerExecAsync(string containerId, string command, CancellationToken cancellationToken)
    {
        return RunDockerCommandAsync($"exec {containerId} {command}", cancellationToken);
    }

    private static async Task<string> RunDockerCommandAsync(string args, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"docker {args} failed (exit code {process.ExitCode}): {await errorTask}");

            return await outputTask;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
