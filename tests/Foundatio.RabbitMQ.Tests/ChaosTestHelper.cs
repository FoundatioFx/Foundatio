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
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException($"Disk alarm on '{resourceName}' did not activate within {timeout.TotalSeconds}s");
    }    public async Task WaitForAlarmClearedAsync(string resourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await HasDiskAlarmAsync(resourceName, cancellationToken))
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException($"Disk alarm on '{resourceName}' did not clear within {timeout.TotalSeconds}s");
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
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(cancellationToken);

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
