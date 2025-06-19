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
