using System;
using System.Threading.Tasks;
using Foundatio.Caching;
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
        public override Task MeasureThroughput() {
            return base.MeasureThroughput();
        }

        [Fact]
        public override Task MeasureSerializerSimpleThroughput() {
            return base.MeasureSerializerSimpleThroughput();
        }

        [Fact]
        public override Task MeasureSerializerComplexThroughput() {
            return base.MeasureSerializerComplexThroughput();
        }
    }
}
