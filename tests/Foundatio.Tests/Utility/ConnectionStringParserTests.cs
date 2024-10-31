using System;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Utility;

public class ConfigurationTests
{
    [Fact]
    public void CanParseConnectionString()
    {
        const string connectionString = "provider=azurestorage;DefaultEndpointsProtocol=https;AccountName=test;AccountKey=nx4TKwaaaaaaaaaa8t51oPyOErc/4N0TOjrMy6aaaaaabDMbFiK+Gf5rLr6XnU1aaaaaqiX2Yik7tvLcwp4lw==;EndpointSuffix=core.windows.net";
        var data = connectionString.ParseConnectionString();
        Assert.Equal(5, data.Count);
        Assert.Equal("azurestorage", data["provider"]);
        Assert.Equal("https", data["DefaultEndpointsProtocol"]);
        Assert.Equal("test", data["AccountName"]);
        Assert.Equal("nx4TKwaaaaaaaaaa8t51oPyOErc/4N0TOjrMy6aaaaaabDMbFiK+Gf5rLr6XnU1aaaaaqiX2Yik7tvLcwp4lw==", data["AccountKey"]);
        Assert.Equal("core.windows.net", data["EndpointSuffix"]);

        Assert.Equal(connectionString, data.BuildConnectionString());
    }

    [Fact]
    public void WillThrowOnInvalidConnectionStrings()
    {
        string connectionString = "provider = azurestorage; = ; DefaultEndpointsProtocol = https   ;";
        Assert.Throws<ArgumentException>(() => connectionString.ParseConnectionString());

        connectionString = "http://localhost:9200";
        Assert.Throws<ArgumentException>(() => connectionString.ParseConnectionString());
    }

    [Fact]
    public void CanParseQuotedConnectionString()
    {
        const string connectionString = "Blah=\"Hey \"\"now\"\" stuff\"";
        var data = connectionString.ParseConnectionString();
        Assert.Single(data);
        Assert.Equal("Hey \"now\" stuff", data["blah"]);

        Assert.Equal(connectionString, data.BuildConnectionString());
    }

    [Fact]
    public void CanParseSimpleConnectionString()
    {
        const string connectionString = "localhost,6379";
        var data = connectionString.ParseConnectionString(defaultKey: "server");
        Assert.Single(data);
        Assert.Equal("localhost,6379", data["server"]);

        Assert.Equal("server=localhost,6379", data.BuildConnectionString());
    }

    [Fact]
    public void CanParseComplexQuotedConnectionString()
    {
        const string connectionString = "Blah=\"foo1=\"\"my value\"\";foo2 =\"\"my value\"\";\"";
        var data = connectionString.ParseConnectionString();
        Assert.Single(data);
        Assert.Equal("foo1=\"my value\";foo2 =\"my value\";", data["Blah"]);

        Assert.Equal(connectionString, data.BuildConnectionString());
    }
}
