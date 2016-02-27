using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Logging;
using Foundatio.Logging.NLog;
using Foundatio.Logging.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Logging {
    public class LoggingTests : TestWithLoggingBase {
        public LoggingTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void BeginScopeProperty() {
            var logger = Log.CreateLogger<LoggingTests>();
            using (logger.BeginScope(b => b.Property("prop1", "val1").Property("prop2", "val2")))
                logger.Info("Hey {Stuff}!", "Eric");


            var entry = Log.LogEntries.First();
            Assert.Equal(1, entry.Scopes.Length);

            var scope1 = entry.Scopes[0];
            Assert.True(scope1 is IDictionary<string, object>);

            var kvp = (IDictionary<string, object>)scope1;
            Assert.True(kvp.ContainsKey("prop2"));
            Assert.Equal("val2", kvp["prop2"]);
        }

        [Fact]
        public void NLogBeginScopeProperty() {
            var provider = new NLogLoggerProvider();
            var logger = provider.CreateLogger("blah");
            using (logger.BeginScope(b => b.Property("prop1", "val1").Property("prop2", "val2")))
            using (logger.BeginScope(b => b.Property("prop1", "innerval1")))
                logger.Info("Hey {Stuff}!", "Eric");
        }

        [Fact]
        public void LogDelegate()
        {
            var logger = Log.CreateLogger<LoggingTests>();
            var name = "Tester";

            logger.Info(() => $"{name} at {DateTime.Now}.");
        }
    }
}
