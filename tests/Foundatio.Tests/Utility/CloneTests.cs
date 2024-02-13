using System;
using System.Collections.Generic;
using Foundatio.Serializer;
using Foundatio.Utility;
using Foundatio.Xunit;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility;

public class CloneTests : TestWithLoggingBase
{
    public CloneTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void CanCloneModel()
    {
        var model = new CloneModel
        {
            IntProperty = 1,
            StringProperty = "test",
            ListProperty = new List<int> { 1 },
            HashSet = new HashSet<string>(),
            ObjectProperty = new CloneModel { IntProperty = 1 }
        };

        var cloned = model.DeepClone();
        Assert.Equal(model.IntProperty, cloned.IntProperty);
        Assert.Equal(model.StringProperty, cloned.StringProperty);
        Assert.Equal(model.ListProperty, cloned.ListProperty);
        Assert.Equal(model.EmptyStringList, cloned.EmptyStringList);
        Assert.Equal(model.EmptyHashSet, cloned.EmptyHashSet);
        Assert.Equal(model.HashSet, cloned.HashSet);
        Assert.Equal(((CloneModel)model.ObjectProperty).IntProperty, ((CloneModel)model.ObjectProperty).IntProperty);
    }

    [Fact]
    public void CanCloneJsonSerializedModel()
    {
        var model = new CloneModel
        {
            IntProperty = 1,
            StringProperty = "test",
            ListProperty = new List<int> { 1 },
            ObjectProperty = new CloneModel { IntProperty = 1 }
        };

        var serializer = new JsonNetSerializer();
        var result = serializer.SerializeToBytes(model);
        var deserialized = serializer.Deserialize<CloneModel>(result);
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

    [Fact]
    public void CanCloneMessagePackSerializedModel()
    {
        var model = new CloneModel
        {
            IntProperty = 1,
            StringProperty = "test",
            ListProperty = new List<int> { 1 },
            ObjectProperty = new CloneModel { IntProperty = 1 }
        };

        var serializer = new MessagePackSerializer();
        var result = serializer.SerializeToBytes(model);
        var deserialized = serializer.Deserialize<CloneModel>(result);
        Assert.Equal(model.IntProperty, deserialized.IntProperty);
        Assert.Equal(model.StringProperty, deserialized.StringProperty);
        Assert.Equal(model.ListProperty, deserialized.ListProperty);
        var dm = (Dictionary<object, object>)deserialized.ObjectProperty;
        Assert.Equal(((CloneModel)model.ObjectProperty).IntProperty, Convert.ToInt32(dm["IntProperty"]));

        var cloned = deserialized.DeepClone();
        Assert.Equal(model.IntProperty, cloned.IntProperty);
        Assert.Equal(model.StringProperty, cloned.StringProperty);
        Assert.Equal(model.ListProperty, cloned.ListProperty);
        var cdm = (Dictionary<object, object>)cloned.ObjectProperty;
        Assert.Equal(((CloneModel)model.ObjectProperty).IntProperty, Convert.ToInt32(cdm["IntProperty"]));
    }
}

public class CloneModel
{
    public int IntProperty { get; set; }
    public string StringProperty { get; set; }
    public List<int> ListProperty { get; set; }
    public IList<string> EmptyStringList { get; } = new List<string>();
    public ISet<string> EmptyHashSet { get; } = new HashSet<string>();
    public ISet<string> HashSet { get; set; }
    public object ObjectProperty { get; set; }
}
