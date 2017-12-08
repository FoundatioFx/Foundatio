using System;
using System.Collections.Generic;
using Foundatio.Logging.Xunit;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
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

            var bytes = serializer.SerializeToBytes(model);
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

    public class SerializeModel {
        public int IntProperty { get; set; }
        public string StringProperty { get; set; }
        public List<int> ListProperty { get; set; }
        public object ObjectProperty { get; set; }
    }
}