using System;
using System.Diagnostics;
using System.Threading;
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Logging;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching {
    public class HybridCacheClientTests: CacheClientTestsBase {
        private readonly ICacheClient _distributedCache = new InMemoryCacheClient();
        private readonly IMessageBus _messageBus = new InMemoryMessageBus();

        public HybridCacheClientTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

        protected override ICacheClient GetCacheClient() {
            return new HybridCacheClient(_distributedCache, _messageBus);
        }

        [Fact]
        public override void CanSetAndGetValue() {
            base.CanSetAndGetValue();
        }

        [Fact]
        public override void CanUseScopedCaches() {
            base.CanUseScopedCaches();
        }

        [Fact]
        public override void CanSetAndGetObject() {
            base.CanSetAndGetObject();
        }

        [Fact]
        public override void CanRemoveByPrefix() {
            base.CanRemoveByPrefix();
        }

        [Fact]
        public override void CanSetExpiration() {
            base.CanSetExpiration();
        }

        [Fact]
        public virtual void WillUseLocalCache() {
            var firstCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(firstCache);

            var secondCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(secondCache);

            firstCache.Set("test", 1);
            Assert.Equal(1, firstCache.LocalCache.Count);
            Assert.Equal(0, secondCache.LocalCache.Count);
            Assert.Equal(0, firstCache.LocalCacheHits);

            Assert.Equal(1, firstCache.Get<int>("test"));
            Assert.Equal(1, firstCache.LocalCacheHits);

            Assert.Equal(1, secondCache.Get<int>("test"));
            Assert.Equal(0, secondCache.LocalCacheHits);
            Assert.Equal(1, secondCache.LocalCache.Count);

            Assert.Equal(1, secondCache.Get<int>("test"));
            Assert.Equal(1, secondCache.LocalCacheHits);
        }

        [Fact]
        public virtual void WillExpireRemoteItems() {
            Logger.Trace().Message("Warm the log...").Write();
            var firstCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(firstCache);

            var secondCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(secondCache);

            firstCache.Set("test", 1, TimeSpan.FromMilliseconds(100));
            Assert.Equal(1, firstCache.LocalCache.Count);
            Assert.Equal(0, secondCache.LocalCache.Count);
            Assert.Equal(0, firstCache.LocalCacheHits);

            Assert.Equal(1, firstCache.Get<int>("test"));
            Assert.Equal(1, firstCache.LocalCacheHits);

            Assert.Equal(1, secondCache.Get<int>("test"));
            Assert.Equal(0, secondCache.LocalCacheHits);
            Assert.Equal(1, secondCache.LocalCache.Count);

            Assert.Equal(1, secondCache.Get<int>("test"));
            Assert.Equal(1, secondCache.LocalCacheHits);

            var sw = new Stopwatch();
            sw.Start();
            while ((firstCache.LocalCache.Count > 0 || secondCache.LocalCache.Count > 0)
                && sw.ElapsedMilliseconds < 500)
                Thread.Sleep(25);
            sw.Stop();
            Trace.WriteLine(sw.Elapsed);
            Assert.Equal(0, firstCache.LocalCache.Count);
            Assert.Equal(0, secondCache.LocalCache.Count);
            Assert.InRange(sw.Elapsed.TotalMilliseconds, 0, 250);
        }
    }
}
