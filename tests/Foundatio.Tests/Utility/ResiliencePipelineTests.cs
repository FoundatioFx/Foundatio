using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Xunit;
using Foundatio.Utility.Resilience;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using System.Collections.Concurrent;
using Polly;
using Polly.Retry;
using Moq;

namespace Foundatio.Tests.Utility;

public class ResiliencePipelineTests : TestWithLoggingBase
{
    private readonly IResiliencePipeline _pipeline;

    public ResiliencePipelineTests(ITestOutputHelper output) : base(output)
    {
        _pipeline = new FoundatioResiliencePipeline { Logger = _logger, MaxAttempts = 5, RetryInterval = TimeSpan.FromMilliseconds(10) };
    }

    [Fact]
    public async Task CanRunWithRetries()
    {
        var task = Task.Run(() =>
        {
            _logger.LogInformation("Hi");
        });

        await task;
        await task;

        await _pipeline.ExecuteAsync(DoStuff, cancellationToken: CancellationToken.None);

        await _pipeline.ExecuteAsync(async () =>
        {
            await DoStuff();
        }, cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task CanRunWithRetriesAndResult()
    {
        var result = await _pipeline.ExecuteAsync(ReturnStuff, cancellationToken: CancellationToken.None);

        Assert.Equal(1, result);

        result = await _pipeline.ExecuteAsync(async () => await ReturnStuff(), cancellationToken: CancellationToken.None);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task CanBoomWithRetries()
    {
        var exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            await _pipeline.ExecuteAsync(() => DoBoom(), cancellationToken: CancellationToken.None);
        });
        Assert.Equal("Hi", exception.Message);

        exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            await _pipeline.ExecuteAsync(async () =>
            {
                await DoBoom();
            }, cancellationToken: CancellationToken.None);
        });
        Assert.Equal("Hi", exception.Message);

        int attempt = 0;
        await _pipeline.ExecuteAsync(() =>
        {
            attempt++;
            return DoBoom(attempt < 5);
        }, cancellationToken: CancellationToken.None);
        Assert.Equal(5, attempt);

        attempt = 0;
        await _pipeline.ExecuteAsync(async () =>
        {
            attempt++;
            await DoBoom(attempt < 5);
        }, cancellationToken: CancellationToken.None);
        Assert.Equal(5, attempt);
    }

    [Fact]
    public async Task CanBoomWithRetriesAndResult()
    {
        var exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            var result = await _pipeline.ExecuteAsync(() => ReturnBoom(), cancellationToken: CancellationToken.None);

            Assert.Equal(1, result);
        });
        Assert.Equal("Hi", exception.Message);

        exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            var result = await _pipeline.ExecuteAsync(async () => await ReturnBoom(), cancellationToken: CancellationToken.None);

            Assert.Equal(1, result);
        });
        Assert.Equal("Hi", exception.Message);

        int attempt = 0;
        var result = await _pipeline.ExecuteAsync(() =>
        {
            attempt++;
            return ReturnBoom(attempt < 5);
        }, cancellationToken: CancellationToken.None);
        Assert.Equal(5, attempt);
        Assert.Equal(1, result);

        attempt = 0;
        result = await _pipeline.ExecuteAsync(async () =>
        {
            attempt++;
            return await ReturnBoom(attempt < 5);
        }, cancellationToken: CancellationToken.None);
        Assert.Equal(5, attempt);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task CanRunWithRetriesAndCancellation()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await _pipeline.ExecuteAsync(DoStuff, cts.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await _pipeline.ExecuteAsync(async () => await DoStuff(), cts.Token);
        });
    }

    [Fact]
    public Task CanRunWithTimeout()
    {
        var pipeline = new FoundatioResiliencePipeline
        {
            Logger = _logger,
            MaxAttempts = 5,
            Timeout = TimeSpan.FromMilliseconds(100)
        };

        return Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await pipeline.ExecuteAsync(async ct =>
            {
                await Task.Delay(500, ct);
            });
        });
    }

    [Fact]
    public void CanUseProvider()
    {
        var provider = new FoundatioResiliencePipelineProvider()
            .WithPipeline("TestPipeline", b => b.WithLogger(_logger).WithMaxAttempts(10).WithRetryInterval(TimeSpan.FromMilliseconds(20)))
            .WithDefaultPipeline(b => b.WithLogger(_logger).WithMaxAttempts(7).WithRetryInterval(TimeSpan.FromMilliseconds(100)).WithJitter());

        // named pipeline
        var pipeline = provider.GetPipeline("TestPipeline");
        Assert.NotNull(pipeline);
        var foundationPipeline = Assert.IsType<FoundatioResiliencePipeline>(pipeline);
        Assert.Equal(_logger, foundationPipeline.Logger);
        Assert.Equal(10, foundationPipeline.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(20), foundationPipeline.RetryInterval);

        // default pipeline
        pipeline = provider.GetPipeline();
        Assert.NotNull(pipeline);
        foundationPipeline = Assert.IsType<FoundatioResiliencePipeline>(pipeline);
        Assert.Equal(_logger, foundationPipeline.Logger);
        Assert.Equal(7, foundationPipeline.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(100), foundationPipeline.RetryInterval);

        // unknown pipeline uses default
        pipeline = provider.GetPipeline("UnknownPipeline");
        Assert.NotNull(pipeline);
        foundationPipeline = Assert.IsType<FoundatioResiliencePipeline>(pipeline);
        Assert.Equal(_logger, foundationPipeline.Logger);
        Assert.Equal(7, foundationPipeline.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(100), foundationPipeline.RetryInterval);
    }

    [Fact]
    public async Task CanUsePolly()
    {
        var pollyResiliencePipelineProvider = new PollyResiliencePipelineProvider()
            .WithPipeline(nameof(ILockProvider.IsLockedAsync), p => p.AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is ApplicationException),
                    Delay = TimeSpan.Zero,
                    MaxRetryAttempts = 5,
                }));

        var mockCacheClient = new Mock<ICacheClient>();
        mockCacheClient.As<IHaveResiliencePipelineProvider>().Setup(c => c.ResiliencePipelineProvider).Returns(pollyResiliencePipelineProvider);
        mockCacheClient.Setup(c => c.AddAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync(true);;

        int hitCount = 0;
        mockCacheClient.Setup(c => c.ExistsAsync(It.IsAny<string>()))
            .Returns(() =>
            {
                if (hitCount < 4)
                {
                    hitCount++;
                    throw new ApplicationException("Simulated failure");
                }
                return Task.FromResult(true);
            });

        var lockProvider = new CacheLockProvider(mockCacheClient.Object, new InMemoryMessageBus());

        var l = await lockProvider.AcquireAsync("test", TimeSpan.FromSeconds(1), TimeSpan.Zero);
        Assert.NotNull(l);
        Assert.True(await lockProvider.IsLockedAsync("test"));

        Assert.Equal(4, hitCount);
    }

    private async ValueTask<int> ReturnStuff()
    {
        await Task.Delay(10);
        return 1;
    }

    private async ValueTask DoStuff()
    {
        await Task.Delay(10);
    }

    private async ValueTask<int> ReturnBoom(bool shouldThrow = true)
    {
        await Task.Delay(10);

        if (shouldThrow)
            throw new ApplicationException("Hi");

        return 1;
    }

    private async ValueTask DoBoom(bool shouldThrow = true)
    {
        await Task.Delay(10);

        if (shouldThrow)
            throw new ApplicationException("Hi");
    }
}

public class PollyResiliencePipelineProvider : IResiliencePipelineProvider
{
    private readonly ConcurrentDictionary<string, IResiliencePipeline> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private IResiliencePipeline _defaultPipeline = new PollyResiliencePipeline(new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions()).Build());

    public IResiliencePipelineProvider WithDefaultPipeline(IResiliencePipeline pipeline)
    {
        _defaultPipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        return this;
    }

    public IResiliencePipelineProvider WithPipeline(string name, ResiliencePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(name);

        ArgumentNullException.ThrowIfNull(pipeline);

        _pipelines[name] = new PollyResiliencePipeline(pipeline);
        return this;
    }

    public IResiliencePipelineProvider WithPipeline(string name, Action<ResiliencePipelineBuilder> pipelineBuilder)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        if (pipelineBuilder == null)
            throw new ArgumentNullException(nameof(pipelineBuilder));

        var builder = new ResiliencePipelineBuilder();
        pipelineBuilder(builder);

        _pipelines[name] = new PollyResiliencePipeline(builder.Build());
        return this;
    }

    public IResiliencePipeline GetPipeline(string name = null)
    {
        return name == null ? _defaultPipeline : _pipelines.GetOrAdd(name, _ => _defaultPipeline);
    }

    private class PollyResiliencePipeline(ResiliencePipeline pipeline) : IResiliencePipeline
    {
        public ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
        {
            return pipeline.ExecuteAsync(action, cancellationToken);
        }

        public ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
        {
            return pipeline.ExecuteAsync(action, cancellationToken);
        }
    }
}
