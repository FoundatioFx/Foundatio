using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility.Resilience;

public interface IResiliencePipelineProvider
{
    IResiliencePipeline GetPipeline(string name = null);
}

public interface IResiliencePipeline
{
    ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);
    ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default);
}

public interface IHaveResiliencePipelineProvider
{
    IResiliencePipelineProvider ResiliencePipelineProvider { get; }
}

public class FoundatioResiliencePipelineProvider : IResiliencePipelineProvider
{
    private readonly ConcurrentDictionary<string, IResiliencePipeline> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private IResiliencePipeline _defaultPipeline;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;

    public FoundatioResiliencePipelineProvider(TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _defaultPipeline = new FoundatioResiliencePipeline(_timeProvider, _loggerFactory.CreateLogger<FoundatioResiliencePipeline>())
        {
            MaxAttempts = 5
        };
    }

    public FoundatioResiliencePipelineProvider WithDefaultPipeline(IResiliencePipeline pipeline)
    {
        _defaultPipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        return this;
    }

    public FoundatioResiliencePipelineProvider WithDefaultPipeline(Action<FoundatioResiliencePipelineBuilder> builder)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        var pipeline = new FoundatioResiliencePipeline(_timeProvider, _loggerFactory.CreateLogger<FoundatioResiliencePipeline>());
        var pipelineBuilder = new FoundatioResiliencePipelineBuilder(pipeline);
        builder(pipelineBuilder);

        _defaultPipeline = pipeline;
        return this;
    }

    public FoundatioResiliencePipelineProvider WithPipeline(string name, IResiliencePipeline pipeline)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        _pipelines[name] = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        return this;
    }

    public FoundatioResiliencePipelineProvider WithPipeline(string name, Action<FoundatioResiliencePipelineBuilder> builder)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        var pipeline = new FoundatioResiliencePipeline(_timeProvider, _loggerFactory.CreateLogger<FoundatioResiliencePipeline>());
        var pipelineBuilder = new FoundatioResiliencePipelineBuilder(pipeline);
        builder(pipelineBuilder);

        _pipelines[name] = pipeline;
        return this;
    }

    public IResiliencePipeline GetPipeline(string name = null)
    {
        if (name == null)
            return _defaultPipeline;

        return _pipelines.TryGetValue(name, out var pipeline) ? pipeline : _defaultPipeline;
    }
}

public class FoundatioResiliencePipeline : IResiliencePipeline
{
    private readonly TimeProvider _timeProvider;
    private ILogger _logger;

    public FoundatioResiliencePipeline(TimeProvider timeProvider = null, ILogger logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        MaxAttempts = 5;
        RetryInterval = null;
    }

    /// <summary>
    /// Gets or sets the logger for this pipeline.
    /// </summary>
    public ILogger Logger {
        get => _logger;
        set => _logger = value ?? NullLogger.Instance;
    }

    /// <summary>
    /// Sets a fixed retry interval for all retries.
    /// </summary>
    public TimeSpan? RetryInterval { get; set; }

    /// <summary>
    /// The maximum number of attempts to execute the action.
    /// </summary>
    public int MaxAttempts { get; set; }

    /// <summary>
    /// A function that determines whether to retry based on the attempt number and exception.
    /// </summary>
    public Func<int, Exception, bool> ShouldRetry { get; set; }

    /// <summary>
    /// Gets or sets a function that returns the backoff interval based on the number of attempts.
    /// </summary>
    public Func<int, TimeSpan> GetBackoffInterval { get; set; } = attempts => TimeSpan.FromMilliseconds(_defaultBackoffIntervals[Math.Min(attempts, _defaultBackoffIntervals.Length - 1)]);

    /// <summary>
    /// Gets or sets a value indicating whether to use jitter in the backoff interval.
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Gets or sets the timeout for the entire operation.
    /// </summary>
    public TimeSpan Timeout { get; set; }

    public async ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        cancellationToken.ThrowIfCancellationRequested();

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        if (Timeout > TimeSpan.Zero)
            linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(Timeout).Token).Token;

        do
        {
            if (attempts > 1)
                _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

            try
            {
                await action(linkedCancellationToken).AnyContext();
                return;
            }
            catch (Exception ex)
            {
                if ((ShouldRetry != null && !ShouldRetry(attempts, ex)) || attempts >= MaxAttempts)
                    throw;

                _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                await _timeProvider.SafeDelay(GetInterval(attempts), linkedCancellationToken).AnyContext();

                ThrowIfTimedOut(startTime);
            }

            attempts++;
        } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

        throw new TaskCanceledException("Should not get here");
    }

    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        cancellationToken.ThrowIfCancellationRequested();

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        if (Timeout > TimeSpan.Zero)
            linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(Timeout).Token).Token;

        do
        {
            if (attempts > 1)
                _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

            try
            {
                return await action(linkedCancellationToken).AnyContext();
            }
            catch (Exception ex)
            {
                if ((ShouldRetry != null && !ShouldRetry(attempts, ex)) || attempts >= MaxAttempts)
                    throw;

                _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                await _timeProvider.SafeDelay(GetInterval(attempts), linkedCancellationToken).AnyContext();

                ThrowIfTimedOut(startTime);
            }

            attempts++;
        } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

        throw new TaskCanceledException("Should not get here");
    }

    private void ThrowIfTimedOut(DateTimeOffset startTime)
    {
        if (Timeout <= TimeSpan.Zero)
            return;

        var elapsed = _timeProvider.GetUtcNow().Subtract(startTime);
        if (elapsed >= Timeout)
            throw new TimeoutException($"Operation timed out after {Timeout:g}.");
    }

    private TimeSpan GetInterval(int attempts)
    {
        var interval = RetryInterval ?? GetBackoffInterval?.Invoke(attempts) ?? TimeSpan.FromMilliseconds(100);

        if (UseJitter)
        {
            double offset = interval.TotalMilliseconds * 0.5 / 2;
            double randomDelay = interval.TotalMilliseconds * 0.5 * _random.NextDouble() - offset;
            double newInterval = interval.TotalMilliseconds + randomDelay;
            interval = TimeSpan.FromMilliseconds(newInterval);
        }

        if (interval < TimeSpan.Zero)
            interval = TimeSpan.Zero;

        return interval;
    }

    private static readonly int[] _defaultBackoffIntervals = [100, 1000, 2000, 2000, 5000, 5000, 10000, 30000, 60000];
    private static readonly Random _random = new();
}

public class FoundatioResiliencePipelineBuilder(FoundatioResiliencePipeline pipeline)
{
    /// <summary>
    /// Sets the logger for the pipeline.
    /// </summary>
    /// <param name="logger"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public FoundatioResiliencePipelineBuilder WithLogger(ILogger logger)
    {
        pipeline.Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    /// <summary>
    /// Sets the maximum number of attempts for the pipeline.
    /// </summary>
    /// <param name="maxAttempts"></param>
    /// <returns></returns>
    public FoundatioResiliencePipelineBuilder WithMaxAttempts(int maxAttempts)
    {
        pipeline.MaxAttempts = maxAttempts;
        return this;
    }

    /// <summary>
    /// Sets a fixed retry interval for all retries.
    /// </summary>
    /// <param name="retryInterval"></param>
    /// <returns></returns>
    public FoundatioResiliencePipelineBuilder WithRetryInterval(TimeSpan? retryInterval)
    {
        pipeline.RetryInterval = retryInterval;
        return this;
    }

    /// <summary>
    /// Sets a function that determines whether to retry based on the attempt number and exception.
    /// </summary>
    /// <param name="shouldRetry"></param>
    /// <returns></returns>
    public FoundatioResiliencePipelineBuilder WithShouldRetry(Func<int, Exception, bool> shouldRetry)
    {
        pipeline.ShouldRetry = shouldRetry;
        return this;
    }

    /// <summary>
    /// Sets a function that returns the backoff interval based on the number of attempts. This overrides the retry interval.
    /// </summary>
    /// <param name="getBackoffInterval"></param>
    /// <returns></returns>
    public FoundatioResiliencePipelineBuilder WithBackoffInterval(Func<int, TimeSpan> getBackoffInterval)
    {
        pipeline.GetBackoffInterval = getBackoffInterval;
        return this;
    }

    /// <summary>
    /// Sets whether to use jitter in the backoff interval.
    /// </summary>
    /// <param name="useJitter"></param>
    /// <returns></returns>
    public FoundatioResiliencePipelineBuilder WithJitter(bool useJitter = true)
    {
        pipeline.UseJitter = useJitter;
        return this;
    }

    /// <summary>
    /// Sets the timeout for the entire operation. If set to zero, no timeout is applied.
    /// </summary>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public FoundatioResiliencePipelineBuilder WithTimeout(TimeSpan timeout)
    {
        pipeline.Timeout = timeout;
        return this;
    }
}

public static class FoundatioResiliencePipelineExtensions
{
    public static IResiliencePipelineProvider GetResiliencePipelineProvider(this object target)
    {
        return target is IHaveResiliencePipelineProvider accessor ? accessor.ResiliencePipelineProvider : null;
    }

    public static ValueTask ExecuteAsync(this IResiliencePipeline pipeline, Func<ValueTask> action, CancellationToken cancellationToken = default)
    {
        if (pipeline == null)
            throw new ArgumentNullException(nameof(pipeline));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return pipeline.ExecuteAsync(_ => action(), cancellationToken);
    }

    public static ValueTask<T> ExecuteAsync<T>(this IResiliencePipeline pipeline, Func<ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        if (pipeline == null)
            throw new ArgumentNullException(nameof(pipeline));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return pipeline.ExecuteAsync(_ => action(), cancellationToken);
    }
}
