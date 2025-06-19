using System;
using System.Linq;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility;

public class TestLoggerTests : TestLoggerBase
{
    private readonly ITestOutputHelper _output;

    public TestLoggerTests(ITestOutputHelper output, TestLoggerFixture fixture) : base(output, fixture)
    {
        _output = output;
        fixture.ConfigureServices(s => s.AddSingleton<SomeClass>());
    }

    [Fact]
    public void CanUseTestLogger()
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddDebug().AddTestLogger(_output, o =>
            {
                o.SetLogLevel("Microsoft", LogLevel.Warning);
            }))
            .AddSingleton<SomeClass>();

        IServiceProvider provider = services.BuildServiceProvider();

        var someClass = provider.GetRequiredService<SomeClass>();

        someClass.DoSomething(1);

        var testLogger = provider.GetTestLogger();
        Assert.Single(testLogger.LogEntries);
        Assert.Contains("Doing something", testLogger.LogEntries[0].Message);

        testLogger.Reset();
        testLogger.SetLogLevel<SomeClass>(LogLevel.Error);

        someClass.DoSomething(2);
        Assert.Empty(testLogger.LogEntries);
    }

    [Fact]
    public void CanUseTestLoggerFixture()
    {
        var someClass = Services.GetRequiredService<SomeClass>();

        for (int i = 1; i <= 9999; i++)
            someClass.DoSomething(i);

        Log.LogInformation("Hello 1");
        Log.LogInformation("Hello 2");

        Assert.Equal(100, TestLogger.LogEntries.Count);
        Assert.Contains("Hello 2", TestLogger.LogEntries.Last().Message);

        Fixture.TestLogger.Reset();
        TestLogger.SetLogLevel<SomeClass>(LogLevel.Error);

        someClass.DoSomething(1002);

        Assert.Empty(TestLogger.LogEntries);
        TestLogger.SetLogLevel<SomeClass>(LogLevel.Information);
    }

    [Fact]
    public void CanUseTestLoggerFixture2()
    {
        var someClass = Services.GetRequiredService<SomeClass>();

        someClass.DoSomething(1);

        Assert.Single(TestLogger.LogEntries);
        Assert.Contains("Doing something", TestLogger.LogEntries[0].Message);

        TestLogger.Reset();
        TestLogger.SetLogLevel<SomeClass>(LogLevel.Error);

        someClass.DoSomething(2);
        Assert.Empty(TestLogger.LogEntries);
    }

    [Fact]
    public void LogLevelsSupportPrefix()
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddDebug().AddTestLogger(_output, o =>
            {
                o.SetLogLevel("Microsoft", LogLevel.Warning);
            }))
            .AddSingleton<SomeClass>();

        IServiceProvider provider = services.BuildServiceProvider();
        var testLogger = provider.GetTestLogger();

        var microsoftLogger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.Test");

        microsoftLogger.LogInformation("This is a test info log message");
        Assert.Empty(testLogger.LogEntries);

        microsoftLogger.LogWarning("This is a test warn log message");
        Assert.Single(testLogger.LogEntries);

        testLogger.Reset();
        testLogger.SetLogLevel("Blah", LogLevel.Information);
        var blahLogger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Blah");
        var blahBlahLogger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Blah.Blah");

        blahLogger.LogInformation("This is a test info log message");
        Assert.Single(testLogger.LogEntries);
        Assert.Contains("This is a test info log message", testLogger.LogEntries[0].Message);
        blahBlahLogger.LogInformation("This is a test info log message");
        Assert.Equal(2, testLogger.LogEntries.Count);
        Assert.Contains("This is a test info log message", testLogger.LogEntries[0].Message);
    }
}

public class SomeClass
{
    private readonly ILogger<SomeClass> _logger;

    public SomeClass(ILogger<SomeClass> logger)
    {
        _logger = logger;
    }

    public void DoSomething(int number)
    {
        _logger.LogInformation("Doing something {Number}", number);
    }
}
