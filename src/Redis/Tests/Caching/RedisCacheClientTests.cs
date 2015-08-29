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
        public async Task MeasureThroughput() {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            await cacheClient.RemoveAllAsync().AnyContext();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++) {
                await cacheClient.SetAsync("test", 13422).AnyContext();
                await cacheClient.SetAsync("flag", true).AnyContext();
                Assert.Equal(13422, await cacheClient.GetAsync<int>("test").AnyContext());
                Assert.Null(await cacheClient.GetAsync<int?>("test2").AnyContext());
                Assert.True(await cacheClient.GetAsync<bool>("flag").AnyContext());
                await metrics.CounterAsync("work").AnyContext();
            }
            metrics.DisplayStats(_writer);
        }

        [Fact]
        public async Task MeasureSerializerSimpleThroughput() {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            await cacheClient.RemoveAllAsync().AnyContext();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++) {
                await cacheClient.SetAsync("test", new SimpleModel {
                    Data1 = "Hello",
                    Data2 = 12
                }).AnyContext();
                var model = await cacheClient.GetAsync<SimpleModel>("test").AnyContext();
                Assert.NotNull(model);
                Assert.Equal("Hello", model.Data1);
                Assert.Equal(12, model.Data2);
                await metrics.CounterAsync("work").AnyContext();
            }
            metrics.DisplayStats();
        }

        [Fact]
        public async Task MeasureSerializerComplexThroughput() {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            await cacheClient.RemoveAllAsync().AnyContext();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++) {
                await cacheClient.SetAsync("test", new ComplexModel {
                    Data1 = "Hello",
                    Data2 = 12,
                    Data3 = true,
                    Simple = new SimpleModel { Data1 = "hi", Data2 = 13 },
                    Simples = new List<SimpleModel> { new SimpleModel { Data1 = "hey", Data2 = 45 }, new SimpleModel { Data1 = "next", Data2 = 3423 } },
                    DictionarySimples = new Dictionary<string, SimpleModel> { { "sdf", new SimpleModel { Data1 = "Sachin" } } }
                }).AnyContext();
                var model = await cacheClient.GetAsync<SimpleModel>("test").AnyContext();
                Assert.NotNull(model);
                Assert.Equal("Hello", model.Data1);
                Assert.Equal(12, model.Data2);
                await metrics.CounterAsync("work").AnyContext();
            }
            metrics.DisplayStats();
        }
    }

    public class SimpleModel {
        public string Data1 { get; set; }
        public int Data2 { get; set; }
    }

    public class ComplexModel {
        public string Data1 { get; set; }
        public int Data2 { get; set; }
        public SimpleModel Simple { get; set; }
        public ICollection<SimpleModel> Simples { get; set; }
        public bool Data3 { get; set; }
        public IDictionary<string, SimpleModel> DictionarySimples { get; set; }
    }
}
