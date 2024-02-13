using System;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility;

public class TestLoggerTests : IClassFixture<TestLoggerFixture>
{
    private readonly ITestOutputHelper _output;
    private readonly TestLoggerFixture _fixture;

    public TestLoggerTests(ITestOutputHelper output, TestLoggerFixture fixture)
    {
        _output = output;
        _fixture = fixture;
        _fixture.Output = output;
        fixture.AddServiceRegistrations(s => s.AddSingleton<SomeClass>());
    }

    [Fact]
    public void CanUseTestLogger()
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddDebug().AddTestLogger(_output))
            .AddSingleton<SomeClass>();

        IServiceProvider provider = services.BuildServiceProvider();

        var someClass = provider.GetRequiredService<SomeClass>();

        someClass.DoSomething();

        var testLogger = provider.GetTestLogger();
        Assert.Single(testLogger.LogEntries);
        Assert.Contains("Doing something", testLogger.LogEntries[0].Message);

        testLogger.Clear();
        testLogger.SetLogLevel<SomeClass>(LogLevel.Error);

        someClass.DoSomething();
        Assert.Empty(testLogger.LogEntries);
    }

    [Fact]
    public void CanUseTestLoggerFixture()
    {
        var someClass = _fixture.Services.GetRequiredService<SomeClass>();

        someClass.DoSomething();

        Assert.Single(_fixture.TestLogger.LogEntries);
        Assert.Contains("Doing something", _fixture.TestLogger.LogEntries[0].Message);

        _fixture.TestLogger.Clear();
        _fixture.TestLogger.SetLogLevel<SomeClass>(LogLevel.Error);

        someClass.DoSomething();
        Assert.Empty(_fixture.TestLogger.LogEntries);
    }

}

public class SomeClass
{
    private readonly ILogger<SomeClass> _logger;

    public SomeClass(ILogger<SomeClass> logger)
    {
        _logger = logger;
    }

    public void DoSomething()
    {
        _logger.LogInformation("Doing something");
    }
}
