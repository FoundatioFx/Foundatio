using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Logging {
    public class LoggingTests : TestWithLoggingBase {
        public LoggingTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void CanLog() {
            var logger = Log.CreateLogger<LoggingTests>();
            using (logger.BeginPropertyScope("prop1", "val1"))
            using (logger.BeginPropertyScope("prop2", "val2")) {
                logger.Info("Hey {Stuff}!", "Eric");
            }

            var entry = Log.LogEntries.First();
            Assert.Equal(2, entry.Scopes.Length);
            var scope1 = entry.Scopes[0];
            Assert.True(scope1 is KeyValuePair<string, string>);
            var kvp = (KeyValuePair<string, string>)scope1;
            Assert.Equal("prop2", kvp.Key);
            Assert.Equal("val2", kvp.Value);
        }
    }
}
