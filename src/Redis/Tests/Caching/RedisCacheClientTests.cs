using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Metrics;
using Foundatio.Tests.Caching;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisCacheClientTests : CacheClientTestsBase {
        private readonly TestOutputWriter _writer;

        public RedisCacheClientTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
            _writer = new TestOutputWriter(output);
        }

        protected override ICacheClient GetCacheClient() {
            return new RedisCacheClient(SharedConnection.GetMuxer());
        }

        [Fact]
        public override Task CanSetAndGetValue() {
            return base.CanSetAndGetValue();
        }
        
        [Fact]
        public override Task CanSetAndGetObject() {
            return base.CanSetAndGetObject();
        }
        
        [Fact]
        public override Task CanTryGet() {
            return base.CanTryGet();
        }

        [Fact]
        public override Task CanSetExpiration() {
            return base.CanSetExpiration();
        }


        [Fact]
        public override Task CanRemoveByPrefix() {
            return base.CanRemoveByPrefix();
        }

        [Fact]
        public override Task CanUseScopedCaches() {
            return base.CanUseScopedCaches();
        }

        [Fact]
        public override void MeasureThroughput() {
            base.MeasureThroughput();
        }

        [Fact]
        public override void MeasureSerializerSimpleThroughput() {
            base.MeasureSerializerSimpleThroughput();
        }

        [Fact]
        public override void MeasureSerializerComplexThroughput() {
            base.MeasureSerializerComplexThroughput();
        }
    }
}
