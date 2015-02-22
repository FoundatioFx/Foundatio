//using System.Collections;
//using System.Collections.Generic;
//using Foundatio.Caching;
//using Foundatio.Metrics;
//using Foundatio.Redis.Cache;
//using Foundatio.Serializer;
//using Foundatio.Tests.Caching;
//using Foundatio.Tests.Utility;
//using StackExchange.Redis;
//using Xunit;

//namespace Foundatio.Redis.Tests.Caching {
//    public class RedisCacheClientTests : CacheClientTestsBase {
//        protected override ICacheClient GetCacheClient() {
//            if (ConnectionStrings.Get("RedisConnectionString") == null)
//                return null;

//            var muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));
//            return new RedisCacheClient(muxer);
//        }

//        [Fact]
//        public override void CanSetAndGetValue() {
//            base.CanSetAndGetValue();
//        }

//        [Fact]
//        public override void CanSetAndGetObject() {
//            base.CanSetAndGetObject();
//        }

//        [Fact]
//        public override void CanSetExpiration() {
//            base.CanSetExpiration();
//        }

//        [Fact]
//        public void MeasureThroughput() {
//            var cacheClient = GetCacheClient();
//            if (cacheClient == null)
//                return;

//            cacheClient.FlushAll();

//            const int itemCount = 10000;
//            var metrics = new InMemoryMetricsClient();
//            for (int i = 0; i < itemCount; i++) {
//                cacheClient.Set("test", 13422);
//                cacheClient.Set("flag", true);
//                Assert.Equal(13422, cacheClient.Get<int>("test"));
//                Assert.Null(cacheClient.Get<int?>("test2"));
//                Assert.True(cacheClient.Get<bool>("flag"));
//                metrics.Counter("work");
//            }
//            metrics.DisplayStats();
//        }

//        [Fact]
//        public void MeasureSerializerSimpleThroughput() {
//            var cacheClient = GetCacheClient();
//            if (cacheClient == null)
//                return;

//            cacheClient.FlushAll();

//            const int itemCount = 10000;
//            var metrics = new InMemoryMetricsClient();
//            for (int i = 0; i < itemCount; i++) {
//                cacheClient.Set("test", new SimpleModel {
//                    Data1 = "Hello",
//                    Data2 = 12
//                });
//                var model = cacheClient.Get<SimpleModel>("test");
//                Assert.NotNull(model);
//                Assert.Equal("Hello", model.Data1);
//                Assert.Equal(12, model.Data2);
//                metrics.Counter("work");
//            }
//            metrics.DisplayStats();
//        }

//        [Fact]
//        public void MeasureSerializerComplexThroughput() {
//            var cacheClient = GetCacheClient();
//            if (cacheClient == null)
//                return;

//            cacheClient.FlushAll();

//            const int itemCount = 10000;
//            var metrics = new InMemoryMetricsClient();
//            for (int i = 0; i < itemCount; i++) {
//                cacheClient.Set("test", new ComplexModel {
//                    Data1 = "Hello",
//                    Data2 = 12,
//                    Data3 = true,
//                    Simple = new SimpleModel { Data1 = "hi", Data2 = 13 },
//                    Simples = new List<SimpleModel> { new SimpleModel { Data1 = "hey", Data2 = 45 }, new SimpleModel { Data1 = "next", Data2 = 3423 } },
//                    DictionarySimples = new Dictionary<string, SimpleModel> { { "sdf", new SimpleModel { Data1 = "Sachin" } } }
//                });
//                var model = cacheClient.Get<SimpleModel>("test");
//                Assert.NotNull(model);
//                Assert.Equal("Hello", model.Data1);
//                Assert.Equal(12, model.Data2);
//                metrics.Counter("work");
//            }
//            metrics.DisplayStats();
//        }
//    }

//    public class SimpleModel {
//        public string Data1 { get; set; }
//        public int Data2 { get; set; }
//    }

//    public class ComplexModel {
//        public string Data1 { get; set; }
//        public int Data2 { get; set; }
//        public SimpleModel Simple { get; set; }
//        public ICollection<SimpleModel> Simples { get; set; }
//        public bool Data3 { get; set; }
//        public IDictionary<string, SimpleModel> DictionarySimples { get; set; }
//    }
//}
