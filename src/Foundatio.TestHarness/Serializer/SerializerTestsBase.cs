using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Foundatio.Logging.Xunit;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Serializer {
    public abstract class SerializerTestsBase : TestWithLoggingBase {
        protected SerializerTestsBase(ITestOutputHelper output) : base(output) {}

        protected virtual ISerializer GetSerializer() {
            return null;
        }

        public virtual void CanRoundTripBytes() {
            var serializer = GetSerializer();
            if (serializer == null)
                return;
            
            var model = new SerializeModel {
                IntProperty = 1,
                StringProperty = "test",
                ListProperty = new List<int> { 1 },
                ObjectProperty = new SerializeModel { IntProperty = 1 }
            };

            byte[] bytes = serializer.SerializeToBytes(model);
            var actual = serializer.Deserialize<SerializeModel>(bytes);
            Assert.Equal(model.IntProperty, actual.IntProperty);
            Assert.Equal(model.StringProperty, actual.StringProperty);
            Assert.Equal(model.ListProperty, actual.ListProperty);

            string text = serializer.SerializeToString(model);
            actual = serializer.Deserialize<SerializeModel>(text);
            Assert.Equal(model.IntProperty, actual.IntProperty);
            Assert.Equal(model.StringProperty, actual.StringProperty);
            Assert.Equal(model.ListProperty, actual.ListProperty);
            Assert.NotNull(model.ObjectProperty);
            Assert.Equal(1, ((dynamic)model.ObjectProperty).IntProperty);
        }

        public virtual void CanRoundTripString() {
            var serializer = GetSerializer();
            if (serializer == null)
                return;
            
            var model = new SerializeModel {
                IntProperty = 1,
                StringProperty = "test",
                ListProperty = new List<int> { 1 },
                ObjectProperty = new SerializeModel { IntProperty = 1 }
            };

            string text = serializer.SerializeToString(model);
            _logger.LogInformation(text);
            var actual = serializer.Deserialize<SerializeModel>(text);
            Assert.Equal(model.IntProperty, actual.IntProperty);
            Assert.Equal(model.StringProperty, actual.StringProperty);
            Assert.Equal(model.ListProperty, actual.ListProperty);
            Assert.NotNull(model.ObjectProperty);
            Assert.Equal(1, ((dynamic)model.ObjectProperty).IntProperty);
        }
    }

    [MemoryDiagnoser]
    [ShortRunJob]
    public abstract class SerializerBenchmarkBase {
        private ISerializer _serializer;
        private readonly SerializeModel _data = new SerializeModel {
            IntProperty = 1,
            StringProperty = "test",
            ListProperty = new List<int> { 1 },
            ObjectProperty = new SerializeModel { IntProperty = 1 }
        };

        private byte[] _serializedData;

        protected abstract ISerializer GetSerializer();

        [GlobalSetup]
        public void Setup() {
            _serializer = GetSerializer();
            _serializedData = _serializer.SerializeToBytes(_data);
        }

        [Benchmark]
        public byte[] Serialize() {
            return _serializer.SerializeToBytes(_data);
        }
        
        [Benchmark]
        public SerializeModel Deserialize() {
            return _serializer.Deserialize<SerializeModel>(_serializedData);
        }

        [Benchmark]
        public SerializeModel RoundTrip() {
            byte[] serializedData = _serializer.SerializeToBytes(_data);
            return _serializer.Deserialize<SerializeModel>(serializedData);
        }
    }

    public class SerializeModel {
        public int IntProperty { get; set; }
        public string StringProperty { get; set; }
        public List<int> ListProperty { get; set; }
        public object ObjectProperty { get; set; }
    }
}