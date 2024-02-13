using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

public static class LoggingExtensions
{
    public static TestLogger GetTestLogger(this IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<TestLogger>();
    }

    public static ILoggingBuilder AddTestLogger(this ILoggingBuilder builder, ITestOutputHelper outputHelper,
        Action<TestLoggerOptions> configure = null)
    {

        var options = new TestLoggerOptions {
            WriteLogEntryFunc = logEntry =>
            {
                outputHelper.WriteLine(logEntry.ToString(false));
            }
        };

        configure?.Invoke(options);

        return builder.AddTestLogger(options);
    }

    public static ILoggingBuilder AddTestLogger(this ILoggingBuilder builder, Action<TestLoggerOptions> configure)
    {
        var options = new TestLoggerOptions();
        configure?.Invoke(options);
        return builder.AddTestLogger(options);
    }

    public static ILoggingBuilder AddTestLogger(this ILoggingBuilder builder, TestLoggerOptions options = null)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        var loggerProvider = new TestLoggerProvider(options);
        builder.AddProvider(loggerProvider);
        builder.Services.TryAddSingleton(loggerProvider.Log);

        return builder;
    }

    public static ILoggerFactory AddTestLogger(this ILoggerFactory factory, Action<TestLoggerOptions> configure = null)
    {
        if (factory == null)
            throw new ArgumentNullException(nameof(factory));

        var options = new TestLoggerOptions();
        configure?.Invoke(options);

        factory.AddProvider(new TestLoggerProvider(options));

        return factory;
    }

    public static TestLogger ToTestLogger(this ITestOutputHelper outputHelper, Action<TestLoggerOptions> configure = null)
    {
        if (outputHelper == null)
            throw new ArgumentNullException(nameof(outputHelper));

        var options = new TestLoggerOptions();
        options.WriteLogEntryFunc = logEntry =>
        {
            outputHelper.WriteLine(logEntry.ToString());
        };

        configure?.Invoke(options);

        var testLogger = new TestLogger(options);

        return testLogger;
    }

    public static ILogger<T> ToTestLogger<T>(this ITestOutputHelper outputHelper, Action<TestLoggerOptions> configure = null)
        => outputHelper.ToTestLogger(configure).CreateLogger<T>();
}
