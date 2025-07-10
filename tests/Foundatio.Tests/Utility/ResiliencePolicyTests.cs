using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Resilience;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Polly;
using Polly.Retry;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility;

public class ResiliencePolicyTests : TestWithLoggingBase
{
    private readonly IResiliencePolicy _policy;

    public ResiliencePolicyTests(ITestOutputHelper output) : base(output)
    {
        _policy = new ResiliencePolicyBuilder().WithLogger(_logger).WithMaxAttempts(5).WithDelay(TimeSpan.FromMilliseconds(10)).Build();
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

        await _policy.ExecuteAsync(DoStuff, cancellationToken: CancellationToken.None);

        await _policy.ExecuteAsync(async () =>
        {
            await DoStuff();
        }, cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task CanRunWithRetriesAndResult()
    {
        var result = await _policy.ExecuteAsync(ReturnStuff, cancellationToken: CancellationToken.None);

        Assert.Equal(1, result);

        result = await _policy.ExecuteAsync(async () => await ReturnStuff(), cancellationToken: CancellationToken.None);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task CanBoomWithRetries()
    {
        var exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            await _policy.ExecuteAsync(() => DoBoom(), cancellationToken: CancellationToken.None);
        });
        Assert.Equal("Hi", exception.Message);

        exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            await _policy.ExecuteAsync(async () =>
            {
                await DoBoom();
            }, cancellationToken: CancellationToken.None);
        });
        Assert.Equal("Hi", exception.Message);

        int attempt = 0;
        await _policy.ExecuteAsync(() =>
        {
            attempt++;
            return DoBoom(attempt < 5);
        }, cancellationToken: CancellationToken.None);
        Assert.Equal(5, attempt);

        attempt = 0;
        await _policy.ExecuteAsync(async () =>
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
            var result = await _policy.ExecuteAsync(() => ReturnBoom(), cancellationToken: CancellationToken.None);

            Assert.Equal(1, result);
        });
        Assert.Equal("Hi", exception.Message);

        exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            var result = await _policy.ExecuteAsync(async () => await ReturnBoom(), cancellationToken: CancellationToken.None);

            Assert.Equal(1, result);
        });
        Assert.Equal("Hi", exception.Message);

        int attempt = 0;
        var result = await _policy.ExecuteAsync(() =>
        {
            attempt++;
            return ReturnBoom(attempt < 5);
        }, cancellationToken: CancellationToken.None);
        Assert.Equal(5, attempt);
        Assert.Equal(1, result);

        attempt = 0;
        result = await _policy.ExecuteAsync(async () =>
        {
            attempt++;
            return await ReturnBoom(attempt < 5);
        }, cancellationToken: CancellationToken.None);
        Assert.Equal(5, attempt);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task CanHandleSpecificExceptionsWithShouldRetry()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithLogger(_logger)
            .WithDelay(TimeSpan.Zero)
            .WithShouldRetry((attempts, ex) => attempts < 3 && ex is ApplicationException)
            .Build();

        int attempt = 0;
        var exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            await policy.ExecuteAsync(() =>
            {
                attempt++;
                throw new ApplicationException("Simulated failure");
            }, cancellationToken: CancellationToken.None);
        });

        Assert.Equal("Simulated failure", exception.Message);
        Assert.Equal(3, attempt);

        attempt = 0;
        var argumentException = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await policy.ExecuteAsync(() =>
            {
                attempt++;
                throw new ArgumentException("Unhandled exception type");
            }, cancellationToken: CancellationToken.None);
        });

        Assert.Equal("Unhandled exception type", argumentException.Message);
        Assert.Equal(1, attempt);
    }

    [Fact]
    public async Task CanHandleSpecificExceptionsWithException()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithLogger(_logger)
            .WithDelay(TimeSpan.Zero)
            .WithMaxAttempts(3)
            .WithUnhandledException<ApplicationException>()
            .Build();

        int attempt = 0;
        var exception = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            await policy.ExecuteAsync(() =>
            {
                attempt++;
                throw new ApplicationException("Simulated failure");
            }, cancellationToken: CancellationToken.None);
        });

        Assert.Equal("Simulated failure", exception.Message);
        Assert.Equal(1, attempt);

        attempt = 0;
        var argumentException = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await policy.ExecuteAsync(() =>
            {
                attempt++;
                throw new ArgumentException("Unhandled exception type");
            }, cancellationToken: CancellationToken.None);
        });

        Assert.Equal("Unhandled exception type", argumentException.Message);
        Assert.Equal(3, attempt);
    }

    [Fact]
    public async Task CanRunWithRetriesAndCancellation()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await _policy.ExecuteAsync(DoStuff, cts.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await _policy.ExecuteAsync(async () => await DoStuff(), cts.Token);
        });
    }

    [Fact]
    public Task CanRunWithTimeout()
    {
        var policy = new ResiliencePolicy
        {
            Logger = _logger,
            MaxAttempts = 5,
            Timeout = TimeSpan.FromMilliseconds(100)
        };

        return Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await policy.ExecuteAsync(async ct =>
            {
                await Task.Delay(500, ct);
            });
        });
    }

    [Fact]
    public void CanUseProvider()
    {
        var provider = new ResiliencePolicyProvider()
            .WithPolicy("TestPolicy", p => p.WithLogger(_logger).WithMaxAttempts(10).WithDelay(TimeSpan.FromMilliseconds(20)))
            .WithPolicy("AnotherPolicy", p => p.WithLogger(_logger).WithMaxAttempts(5).WithDelay(TimeSpan.FromMilliseconds(50)).WithCircuitBreaker(c => c.WithMinimumCalls(1000)))
            .WithDefaultPolicy(p => p.WithLogger(_logger).WithMaxAttempts(7).WithDelay(TimeSpan.FromMilliseconds(100)).WithJitter());

        // named policy
        var policy = provider.GetPolicy("TestPolicy");
        Assert.NotNull(policy);
        var resiliencePolicy = Assert.IsType<ResiliencePolicy>(policy);
        Assert.Equal(_logger, resiliencePolicy.Logger);
        Assert.Equal(10, resiliencePolicy.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(20), resiliencePolicy.Delay);

        // default policy
        policy = provider.GetDefaultPolicy();
        Assert.NotNull(policy);
        resiliencePolicy = Assert.IsType<ResiliencePolicy>(policy);
        Assert.Equal(_logger, resiliencePolicy.Logger);
        Assert.Equal(7, resiliencePolicy.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(100), resiliencePolicy.Delay);

        // unknown policy uses default
        policy = provider.GetPolicy("UnknownPolicy");
        Assert.NotNull(policy);
        resiliencePolicy = Assert.IsType<ResiliencePolicy>(policy);
        Assert.Equal(_logger, resiliencePolicy.Logger);
        Assert.Equal(7, resiliencePolicy.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(100), resiliencePolicy.Delay);
    }

    [Fact]
    public async Task CanUseCircuitBreaker()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var resiliencePolicyProvider = new ResiliencePolicyProvider(timeProvider, Log)
            .WithPolicy("MyPolicy", p => p.WithMaxAttempts(1).WithCircuitBreaker(b => b.WithMinimumCalls(10).WithBreakDuration(TimeSpan.FromSeconds(5))));

        var policy = resiliencePolicyProvider.GetPolicy("MyPolicy") as ResiliencePolicy;
        Assert.NotNull(policy);

        Assert.Equal(CircuitState.Closed, policy.CircuitBreaker.State);

        // send 10 successful calls
        for (int i = 0; i < 10; i++)
            await policy.ExecuteAsync(DoStuff);

        // send 1 error call before the circuit breaker is triggered
        await Assert.ThrowsAsync<ApplicationException>(async () => await policy.ExecuteAsync(() => DoBoom()));
        Assert.Equal(CircuitState.Closed, policy.CircuitBreaker.State);

        // send 1 more error call to trigger the circuit breaker
        await Assert.ThrowsAsync<ApplicationException>(async () => await policy.ExecuteAsync(() => DoBoom()));
        Assert.Equal(CircuitState.Open, policy.CircuitBreaker.State);

        // send 10 error calls and ensure they throw BrokenCircuitException
        for (int i = 0; i < 10; i++)
            await Assert.ThrowsAsync<BrokenCircuitException>(async () => await policy.ExecuteAsync(() => DoBoom()));

        Assert.Equal(CircuitState.Open, policy.CircuitBreaker.State);

        // advance past the circuit break duration
        timeProvider.Advance(TimeSpan.FromSeconds(10));

        // send 10 successful calls to close the circuit breaker
        for (int i = 0; i < 10; i++)
            await policy.ExecuteAsync(DoStuff);

        Assert.Equal(CircuitState.Closed, policy.CircuitBreaker.State);
    }

    [Fact(Skip = "Using this to test circuit breaker sharing in parallel, not a real test")]
    public async Task CanShareCircuitBreaker()
    {
        var timeProvider = TimeProvider.System;
        var circuitBreaker = new CircuitBreakerBuilder(_logger, timeProvider)
            .WithMinimumCalls(100)
            .WithBreakDuration(TimeSpan.FromSeconds(1))
            .Build();

        var resiliencePolicyProvider = new ResiliencePolicyProvider(timeProvider, Log)
            .WithPolicy("MyPolicy1", p => p.WithMaxAttempts(2).WithCircuitBreaker(circuitBreaker))
            .WithPolicy("MyPolicy2", p => p.WithMaxAttempts(2).WithDelay(TimeSpan.FromMilliseconds(1)).WithCircuitBreaker(circuitBreaker));

        var policy1 = resiliencePolicyProvider.GetPolicy("MyPolicy1") as ResiliencePolicy;
        Assert.NotNull(policy1);

        var policy2 = resiliencePolicyProvider.GetPolicy("MyPolicy2") as ResiliencePolicy;
        Assert.NotNull(policy2);

        Assert.Same(policy1.CircuitBreaker, policy2.CircuitBreaker);

        var task1 = Task.Run(async () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                try
                {
                    await policy1.ExecuteAsync(DoStuff);
                }
                catch (BrokenCircuitException)
                {
                    // ignore
                }

                await Task.Delay(1);
            }

            _logger.LogInformation("Done with task1");
        });

        var task2 = Task.Run(async () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                try
                {
                    await policy2.ExecuteAsync(async () => await DoBoom());
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            _logger.LogInformation("Done with task2");
        });

        await Task.WhenAll(task1, task2);

        await Task.Yield();
    }

    [Fact]
    public async Task CanShareCircuitBreakerInParallel()
    {
        var timeProvider = TimeProvider.System;
        var circuitBreaker = new CircuitBreakerBuilder(_logger, timeProvider)
            .WithMinimumCalls(100)
            .WithBreakDuration(TimeSpan.FromSeconds(5))
            .Build();

        var resiliencePolicyProvider = new ResiliencePolicyProvider(timeProvider, Log)
            .WithPolicy("MyPolicy1", p => p.WithMaxAttempts(1).WithCircuitBreaker(circuitBreaker))
            .WithPolicy("MyPolicy2", p => p.WithMaxAttempts(1).WithCircuitBreaker(circuitBreaker));

        var policy1 = resiliencePolicyProvider.GetPolicy("MyPolicy1") as ResiliencePolicy;
        Assert.NotNull(policy1);

        var policy2 = resiliencePolicyProvider.GetPolicy("MyPolicy2") as ResiliencePolicy;
        Assert.NotNull(policy2);

        Assert.Same(policy1.CircuitBreaker, policy2.CircuitBreaker);

        var task1 = Parallel.ForEachAsync(Enumerable.Range(0, 1000), async (i, ct) =>
        {
            try
            {
                await policy1.ExecuteAsync(DoStuff, ct);
            }
            catch (BrokenCircuitException)
            {
                // ignore exceptions for this test
            }
        });

        var task2 = Parallel.ForEachAsync(Enumerable.Range(0, 1000), async (i, ct) =>
        {
            try
            {
                await policy1.ExecuteAsync(async () => await DoBoom(), ct);
            }
            catch (BrokenCircuitException)
            {
            }
            catch (ApplicationException)
            {
            }
        });

        await Task.WhenAll(task1, task2);

        await Task.Yield();
    }

    [Fact]
    public async Task CanUsePolly()
    {
        // replacing the policy for ILockProvider with a Polly pipeline
        var pollyResiliencePolicyProvider = new PollyResiliencePolicyProvider()
            .WithPolicy<ILockProvider>(p => p.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is ApplicationException),
                Delay = TimeSpan.Zero,
                MaxRetryAttempts = 5,
            }));

        var mockCacheClient = new Mock<ICacheClient>();
        mockCacheClient.As<IHaveResiliencePolicyProvider>().Setup(c => c.ResiliencePolicyProvider).Returns(pollyResiliencePolicyProvider);
        mockCacheClient.Setup(c => c.AddAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync(true);

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
