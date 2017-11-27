using System;
using System.Collections.Generic;
using Foundatio.Logging.Xunit;
using Foundatio.Serializer;
using Foundatio.Utility;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class CloneTestsTests : TestWithLoggingBase {
        public CloneTestsTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void CanCloneModel() {
            var model = new CloneModel {
                IntProperty = 1,
                StringProperty = "test",
                ListProperty = new List<int> { 1 },
                ObjectProperty = new CloneModel { IntProperty =  1 }
            };

            var cloned = model.DeepClone();
            Assert.Equal(model.IntProperty, cloned.IntProperty);
            Assert.Equal(model.StringProperty, cloned.StringProperty);
            Assert.Equal(model.ListProperty, cloned.ListProperty);
            Assert.Equal(((CloneModel)model.ObjectProperty).IntProperty, ((CloneModel)model.ObjectProperty).IntProperty);
        }

        [Fact]
        public void CanCloneSerializedModel() {
            var model = new CloneModel {
                IntProperty = 1,
                StringProperty = "test",
                ListProperty = new List<int> { 1 },
                ObjectProperty = new CloneModel { IntProperty = 1 }
            };

            var serializer = new JsonNetSerializer();
            string json = serializer.SerializeToString(model);
            var deserialized = serializer.Deserialize<CloneModel>(json);
            Assert.Equal(model.IntProperty, deserialized.IntProperty);
            Assert.Equal(model.StringProperty, deserialized.StringProperty);
            Assert.Equal(model.ListProperty, deserialized.ListProperty);
            var dm = ((JToken)deserialized.ObjectProperty).ToObject<CloneModel>();
            Assert.Equal(((CloneModel)model.ObjectProperty).IntProperty, dm.IntProperty);

            var cloned = deserialized.DeepClone();
            Assert.Equal(model.IntProperty, cloned.IntProperty);
            Assert.Equal(model.StringProperty, cloned.StringProperty);
            Assert.Equal(model.ListProperty, cloned.ListProperty);
            var cdm = ((JToken)cloned.ObjectProperty).ToObject<CloneModel>();
            Assert.Equal(((CloneModel)model.ObjectProperty).IntProperty, cdm.IntProperty);
        }

        private class CloneModel {
            public int IntProperty { get; set; }
            public string StringProperty { get; set; }
            public List<int> ListProperty { get; set; }
            public object ObjectProperty { get; set; }
        }
    }
}