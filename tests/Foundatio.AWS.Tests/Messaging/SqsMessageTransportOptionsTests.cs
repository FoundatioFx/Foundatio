using System.Reflection;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.AWS.Tests.Messaging;

public class SqsMessageTransportOptionsTests
{
    [Theory]
    [InlineData("SecretKey")]
    [InlineData("secret key")]
    [InlineData("SecretAccessKey")]
    [InlineData(" Secret Access Key ")]
    [InlineData("Secret")]
    public void ToString_WithASecretInTheConnectionString_RedactsOnlyTheSecret(string secretName)
    {
        // Arrange
        var options = new SqsMessageTransportOptions
        {
            ConnectionString = $"ServiceUrl=http://localhost:4566;AccessKey=AKIDEXAMPLE;{secretName}=wJalrXUtnFEMI/K7MDENG;Region=us-east-1",
            NamePrefix = "dev-"
        };

        // Act
        string text = options.ToString();

        // Assert
        Assert.DoesNotContain("wJalrXUtnFEMI", text);
        Assert.Contains($"{secretName}=***", text);
        Assert.Contains("ServiceUrl=http://localhost:4566;AccessKey=AKIDEXAMPLE;", text);
        Assert.Contains(";Region=us-east-1", text);
        foreach (var property in typeof(SqsMessageTransportOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.Contains($"{property.Name} = ", text);
    }

    [Fact]
    public void ToString_WithoutAConnectionString_PrintsTheOtherOptions()
    {
        // Arrange
        var options = new SqsMessageTransportOptions { NamePrefix = "dev-", Fifo = true };

        // Act
        string text = options.ToString();

        // Assert
        Assert.Contains("NamePrefix = dev-", text);
        Assert.Contains("Fifo = True", text);
    }
}
