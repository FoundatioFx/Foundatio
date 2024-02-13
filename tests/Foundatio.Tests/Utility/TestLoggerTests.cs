using System;
using System.Collections.Generic;
using Foundatio.Serializer;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility;

public class TestLoggerTests
{
    private readonly ITestOutputHelper _output;

    public TestLoggerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CanUseTestLogger()
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddDebug().AddXUnit(_output))
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
