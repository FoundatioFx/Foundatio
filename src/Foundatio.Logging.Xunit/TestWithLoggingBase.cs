using System;
using Xunit.Abstractions;

namespace Foundatio.Logging.Xunit {
    public abstract class TestWithLoggingBase {
        protected readonly ILogger _logger;

        protected TestWithLoggingBase(ITestOutputHelper output) {
            Log = new TestLoggerFactory(output);
            _logger = Log.CreateLogger(GetType());
        }

        protected TestLoggerFactory Log { get; }
    }
}