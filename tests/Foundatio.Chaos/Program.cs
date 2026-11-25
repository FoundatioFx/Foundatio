using Foundatio.Chaos;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length == 0 || args[0] is not ("publish" or "consume" or "verify"))
{
    Console.Error.WriteLine("usage: Foundatio.Chaos publish|consume|verify --transport memory|rabbitmq|redis|kafka|sqs|asb [--name value]... (see README.md)");
    return 2;
}

string role = args[0];
var settings = new ChaosSettings(args[1..]);
using var ledger = new Ledger(settings.RunDirectory, settings.Get("name", role));
ledger.Write("X", "start", role, settings.Transport, Environment.ProcessId);

using var host = ChaosHost.Build(settings, ledger, consume: role == "consume");
await host.StartAsync();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Foundatio.Chaos");
int exitCode = 0;

switch (role)
{
    case "publish":
        var publisher = new Publisher(
            host.Services.GetRequiredService<IMessageClient>(),
            settings.Jobs ? host.Services.GetRequiredService<IJobClient>() : null,
            ledger, settings, host.Services.GetRequiredService<ILogger<Publisher>>());
        await publisher.RunAsync(lifetime.ApplicationStopping);
        break;
    case "consume":
        logger.LogInformation("Consuming on {Transport} with prefix {Prefix}; stop with SIGTERM", settings.Transport, settings.Prefix);
        await host.WaitForShutdownAsync();
        break;
    case "verify":
        exitCode = await new Verifier(host.Services, settings).RunAsync(lifetime.ApplicationStopping);
        break;
}

ledger.Write("X", "stop", role);
await host.StopAsync();
return exitCode;
