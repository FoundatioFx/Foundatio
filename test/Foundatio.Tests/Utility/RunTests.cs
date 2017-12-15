using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Logging.Xunit;
using Foundatio.Serializer;
using Foundatio.Utility;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class RunTests : TestWithLoggingBase {
        public RunTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public async Task CanRunWithRetries() {
            int count = 0;
            await Assert.ThrowsAsync<ApplicationException>(async () => await Run.WithRetriesAsync(() => {
                count++;
                throw new ApplicationException();
            }, 5));
            
            Assert.Equal(5, count);
        }
    }
}