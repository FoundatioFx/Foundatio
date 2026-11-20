using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests.Jobs;

public class RedisJobsBuilderExtensionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Fact]
    public async Task UseRedis_WithARegisteredConnection_RunsAnEnqueuedJob()
    {
        // Arrange
        var muxer = SharedConnection.GetMuxer(Log);
        Assert.SkipWhen(muxer is null, "RedisConnectionString is not configured.");
        string prefix = $"foundatio-tests:helpers-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton<IConnectionMultiplexer>(muxer);
        services.AddFoundatioJobs(j => j
            .UseRedis(_ => new RedisJobStoreOptions { Prefix = prefix })
            .AddJob<GreetJob>());
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(TestCancellationToken);

        // Act
        var handle = await provider.GetRequiredService<IJobClient>().EnqueueAsync(new Greet("Redis"), cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(30), TestCancellationToken);
        var store = provider.GetRequiredService<IJobStore>();
        foreach (var hostedService in Enumerable.Reverse(hostedServices))
            await hostedService.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();

        // Assert
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal("Hello, Redis", job.Message);
        Assert.IsType<RedisJobStore>(store);
        Assert.True(await muxer.GetDatabase().KeyExistsAsync($"{{{prefix}}}:job:{job.Id}"));
    }

    [Fact]
    public async Task UseRedis_WithoutARegisteredConnection_ThrowsWhenTheStoreIsCreated()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFoundatioJobs(j => j.UseRedis());
        await using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IJobStore>());

        // Assert
        Assert.Contains(nameof(IConnectionMultiplexer), exception.Message);
    }

    public record Greet(string Name);

    public class GreetJob : IJobHandler<Greet>
    {
        public Task HandleAsync(Greet args, IJobContext context, CancellationToken cancellationToken)
            => context.ReportProgressAsync(100, $"Hello, {args.Name}", cancellationToken);
    }
}
