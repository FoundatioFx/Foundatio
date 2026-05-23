using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddRabbitMQ("messaging")
    .WithManagementPlugin();

builder.AddContainer("messaging-delayed", "foundatiorabbitmq-rabbitmq-delayed", "latest")
    .WithEndpoint(targetPort: 5672, name: "amqp", scheme: "tcp")
    .WithEndpoint(targetPort: 15672, name: "management", scheme: "http");

var containerMemoryLimits = new[] { "384m", "448m", "512m" };
var chaosHostnames = new[] { "chaos1", "chaos2", "chaos3" };
var chaosNodes = new List<IResourceBuilder<ContainerResource>>(3);

for (int nodeIndex = 0; nodeIndex < 3; nodeIndex++)
{
    string hostname = chaosHostnames[nodeIndex];
    var chaosNode = builder.AddContainer($"chaos-{nodeIndex + 1}", "rabbitmq", "4.2.2-management")
        .WithContainerNetworkAlias(hostname)
        .WithContainerRuntimeArgs($"--memory={containerMemoryLimits[nodeIndex]}", "--hostname", hostname)
        .WithEnvironment("RABBITMQ_DEFAULT_USER", "guest")
        .WithEnvironment("RABBITMQ_DEFAULT_PASS", "guest")
        .WithEnvironment("RABBITMQ_NODENAME", $"rabbit@{hostname}")
        .WithEnvironment("RABBITMQ_ERLANG_COOKIE", "aspire-chaos-cluster-cookie")
        .WithBindMount($"config/chaos-{nodeIndex + 1}.conf", "/etc/rabbitmq/conf.d/99-limits.conf", isReadOnly: true)
        .WithEndpoint(targetPort: 5672, name: "amqp", scheme: "tcp")
        .WithEndpoint(targetPort: 15672, name: "management", scheme: "http")
        .WithEndpoint(targetPort: 15692, name: "prometheus", scheme: "http")
        .WithHttpHealthCheck("/metrics", endpointName: "prometheus");

    if (nodeIndex > 0)
        chaosNode.WaitFor(chaosNodes[0]);

    chaosNodes.Add(chaosNode);
}

var chaos1Amqp = chaosNodes[0].GetEndpoint("amqp");
var chaos2Amqp = chaosNodes[1].GetEndpoint("amqp");
var chaos3Amqp = chaosNodes[2].GetEndpoint("amqp");

builder.AddProject<Foundatio_RabbitMQ_Publish>("publisher")
    .WaitFor(chaosNodes[0])
    .WaitFor(chaosNodes[1])
    .WaitFor(chaosNodes[2])
    .WithArgs("--interval", "2000", "--publisher-confirms", "--durable")
    .WithEnvironment(context =>
    {
        context.EnvironmentVariables["ConnectionStrings__messaging"] =
            ReferenceExpression.Create($"amqp://guest:guest@{chaos1Amqp.Property(EndpointProperty.Host)}:{chaos1Amqp.Property(EndpointProperty.Port)}");
        context.EnvironmentVariables["RABBITMQ_HOSTS"] =
            ReferenceExpression.Create($"{chaos1Amqp.Property(EndpointProperty.Host)}:{chaos1Amqp.Property(EndpointProperty.Port)},{chaos2Amqp.Property(EndpointProperty.Host)}:{chaos2Amqp.Property(EndpointProperty.Port)},{chaos3Amqp.Property(EndpointProperty.Host)}:{chaos3Amqp.Property(EndpointProperty.Port)}");
    });

builder.AddProject<Foundatio_RabbitMQ_Subscribe>("subscriber")
    .WaitFor(chaosNodes[0])
    .WaitFor(chaosNodes[1])
    .WaitFor(chaosNodes[2])
    .WithArgs("--durable")
    .WithEnvironment(context =>
    {
        context.EnvironmentVariables["ConnectionStrings__messaging"] =
            ReferenceExpression.Create($"amqp://guest:guest@{chaos1Amqp.Property(EndpointProperty.Host)}:{chaos1Amqp.Property(EndpointProperty.Port)}");
        context.EnvironmentVariables["RABBITMQ_HOSTS"] =
            ReferenceExpression.Create($"{chaos1Amqp.Property(EndpointProperty.Host)}:{chaos1Amqp.Property(EndpointProperty.Port)},{chaos2Amqp.Property(EndpointProperty.Host)}:{chaos2Amqp.Property(EndpointProperty.Port)},{chaos3Amqp.Property(EndpointProperty.Host)}:{chaos3Amqp.Property(EndpointProperty.Port)}");
    });

foreach (var node in chaosNodes)
{
    AddChaosCommand(node, "trigger-disk-alarm", "Trigger Disk Alarm",
        "rabbitmqctl set_disk_free_limit 999GB",
        new() { IconName = "AlertOn", IconVariant = IconVariant.Filled, ConfirmationMessage = "Trigger disk alarm on this node?" });

    AddChaosCommand(node, "clear-disk-alarm", "Clear Disk Alarm",
        "rabbitmqctl set_disk_free_limit 10MB",
        new() { IconName = "AlertOff", IconVariant = IconVariant.Filled, ConfirmationMessage = "Clear disk alarm on this node?" });

    AddChaosCommand(node, "trigger-memory-alarm", "Trigger Memory Alarm",
        "rabbitmqctl set_vm_memory_high_watermark 0.0001",
        new() { IconName = "Warning", IconVariant = IconVariant.Filled, ConfirmationMessage = "Trigger memory alarm on this node?" });

    AddChaosCommand(node, "clear-memory-alarm", "Clear Memory Alarm",
        "rabbitmqctl set_vm_memory_high_watermark 0.8",
        new() { IconName = "Checkmark", IconVariant = IconVariant.Filled, ConfirmationMessage = "Clear memory alarm on this node?" });

    AddChaosCommand(node, "close-all-connections", "Close All Connections",
        "rabbitmqctl close_all_connections chaos-test",
        new() { IconName = "PlugDisconnected", IconVariant = IconVariant.Filled, ConfirmationMessage = "Force-close all connections on this node?" });
}

chaosNodes[0].WithCommand("trigger-all-disk-alarms", "Trigger ALL Disk Alarms", async _ =>
{
    try
    {
        foreach (var node in chaosNodes)
            await DockerExecAsync(node.Resource.Name, "rabbitmqctl set_disk_free_limit 999GB");
        return CommandResults.Success();
    }
    catch (Exception ex)
    {
        return CommandResults.Failure(ex.Message);
    }
}, new() { IconName = "AlertUrgent", IconVariant = IconVariant.Filled, ConfirmationMessage = "Trigger disk alarms on ALL nodes? This will block ALL publishing." });

chaosNodes[0].WithCommand("clear-all-disk-alarms", "Clear ALL Disk Alarms", async _ =>
{
    try
    {
        foreach (var node in chaosNodes)
            await DockerExecAsync(node.Resource.Name, "rabbitmqctl set_disk_free_limit 10MB");
        return CommandResults.Success();
    }
    catch (Exception ex)
    {
        return CommandResults.Failure(ex.Message);
    }
}, new() { IconName = "DismissCircle", IconVariant = IconVariant.Filled, ConfirmationMessage = "Clear disk alarms on ALL nodes?" });

await builder.Build().RunAsync();


static void AddChaosCommand(IResourceBuilder<ContainerResource> node, string name, string display, string rabbitmqCommand, CommandOptions options)
{
    node.WithCommand(name, display, async context =>
    {
        try
        {
            await DockerExecAsync(context.ResourceName, rabbitmqCommand);
            return CommandResults.Success();
        }
        catch (Exception ex)
        {
            return CommandResults.Failure(ex.Message);
        }
    }, options);
}

static async Task DockerExecAsync(string resourceName, string command)
{
    string containerId = await RunDockerAsync($"ps -q --filter \"name={resourceName}\"");
    if (String.IsNullOrWhiteSpace(containerId))
        throw new InvalidOperationException($"Container '{resourceName}' not found");

    var trimmedId = containerId.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
    await RunDockerAsync($"exec {trimmedId} {command}");
}

static async Task<string> RunDockerAsync(string args)
{
    using var process = new System.Diagnostics.Process();
    process.StartInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "docker",
        Arguments = args,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    process.Start();

    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await Task.WhenAll(outputTask, errorTask);
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
        throw new InvalidOperationException($"docker {args} failed: {(await errorTask).Trim()}");

    return await outputTask;
}
