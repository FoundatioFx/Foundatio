using Foundatio.Caching;
using Foundatio.Tests.Caching;
using Foundatio.Tests.Utility;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Caching {
    public class AzureCacheClientTests : CacheClientTestsBase {
        public AzureCacheClientTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

        protected override ICacheClient GetCacheClient() {
            if (ConnectionStrings.Get("AzureConnectionString") == null)
                return null;

            //var muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("AzureConnectionString"));
            //return new RedisCacheClient(muxer);
            return null;
        }
    }
}
