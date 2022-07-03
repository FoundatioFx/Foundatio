using Foundatio.Xunit;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;
using System.Text.Json;
using System.Text;
using Foundatio.Serializer;
using System;

namespace Foundatio.Tests.Utility {
    public class DataDictionaryTests : TestWithLoggingBase {
        public DataDictionaryTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void CanGetData() {
            var serializer = new SystemTextJsonSerializer();

            var model = new MyModel();
            var old = new MyDataModel { IntProperty = 12, StringProperty = "Kelly" };
            model.Data["Old"] = old;
            model.Data["OldJson"] = JsonSerializer.Serialize(old);
            model.Data["OldBytes"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(old));
            model.Data["New"] = new MyDataModel { IntProperty = 17, StringProperty = "Allen" };
            model.Data["Int16"] = (short)12;
            model.Data["Int32"] = 12;
            model.Data["Int64"] = 12L;
            model.Data["bool"] = true;

            var dataOld = model.GetDataOrDefault<MyDataModel>("Old");
            Assert.Same(old, dataOld);

            Assert.Throws<ArgumentException>(() => model.GetDataOrDefault<MyDataModel>("OldJson"));

            var jsonDataOld = model.GetDataOrDefault<MyDataModel>("OldJson", serializer: serializer);
            Assert.Equal(12, jsonDataOld.IntProperty);
            Assert.Equal("Kelly", jsonDataOld.StringProperty);

            var bytesDataOld = model.GetDataOrDefault<MyDataModel>("OldBytes", serializer: serializer);
            Assert.Equal(12, bytesDataOld.IntProperty);
            Assert.Equal("Kelly", bytesDataOld.StringProperty);

            model.Serializer = serializer;

            jsonDataOld = model.GetDataOrDefault<MyDataModel>("OldJson");
            Assert.Equal(12, jsonDataOld.IntProperty);
            Assert.Equal("Kelly", jsonDataOld.StringProperty);

            Assert.True(model.TryGetData("OldJson", out jsonDataOld));
            Assert.Equal(12, jsonDataOld.IntProperty);
            Assert.Equal("Kelly", jsonDataOld.StringProperty);
            Assert.False(model.TryGetData("OldJson2", out jsonDataOld));

            bytesDataOld = model.GetDataOrDefault<MyDataModel>("OldBytes");
            Assert.Equal(12, bytesDataOld.IntProperty);
            Assert.Equal("Kelly", bytesDataOld.StringProperty);

            Assert.Equal(12, model.GetDataOrDefault<short>("Int16"));
            Assert.Equal(12, model.GetDataOrDefault<short>("Int32"));
            Assert.Equal(12, model.GetDataOrDefault<short>("Int64"));

            Assert.Equal(12, model.GetDataOrDefault<int>("Int16"));
            Assert.Equal(12, model.GetDataOrDefault<int>("Int32"));
            Assert.Equal(12, model.GetDataOrDefault<int>("Int64"));

            Assert.Equal(12, model.GetDataOrDefault<long>("Int16"));
            Assert.Equal(12, model.GetDataOrDefault<long>("Int32"));
            Assert.Equal(12, model.GetDataOrDefault<long>("Int64"));
            
            Assert.Equal(1, model.GetDataOrDefault<long>("bool"));
        }
    }

    public class MyModel : IHaveData, IHaveSerializer {
        public int IntProperty { get; set; }
        public string StringProperty { get; set; }
        public IDataDictionary Data { get; } = new DataDictionary();

        public ISerializer Serializer { get; set; } 
    }

    public class MyDataModel {
        public int IntProperty { get; set; }
        public string StringProperty { get; set; }
    }
}