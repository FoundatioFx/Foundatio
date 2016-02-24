using System;
using Foundatio.Logging;
using Xunit.Abstractions;

namespace Foundatio.Tests.Logging {
    public abstract class TestWithLoggingBase {
        protected readonly ILogger _logger;

        protected TestWithLoggingBase(ITestOutputHelper output) {
            LoggerFactory = new TestLoggerFactory(output);
            _logger = LoggerFactory.CreateLogger(GetType());
        }

        protected TestLoggerFactory LoggerFactory { get; }
    }
}