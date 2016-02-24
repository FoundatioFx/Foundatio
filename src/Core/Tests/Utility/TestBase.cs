using System;
using Foundatio.Logging;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public abstract class TestBase {
        protected readonly ILogger _logger;

        protected TestBase(ITestOutputHelper output) {
            LoggerFactory = new TestLoggerFactory(output);
            _logger = LoggerFactory.CreateLogger(GetType());
        }

        protected TestLoggerFactory LoggerFactory { get; }
    }
}