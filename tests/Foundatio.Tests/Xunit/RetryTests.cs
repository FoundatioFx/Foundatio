using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Xunit;

public class RetryTests : TestWithLoggingBase
{
    private static int _retryFactSucceedsOnSecondAttemptCounter;
    private static int _retryFactSucceedsOnThirdAttemptCounter;
    private static int _retryFactWithCustomRetriesCounter;
    private static int _retryTheorySucceedsOnSecondAttemptCounter;

    public RetryTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public void RetryFactAttribute_DefaultMaxRetries_IsThree()
    {
        // Arrange & Act
        var attribute = new RetryFactAttribute();

        // Assert
        Assert.Equal(3, attribute.MaxRetries);
    }

    [Fact]
    public void RetryFactAttribute_WithCustomMaxRetries_SetsValue()
    {
        // Arrange & Act
        var attribute = new RetryFactAttribute(5);

        // Assert
        Assert.Equal(5, attribute.MaxRetries);
    }

    [Fact]
    public void RetryFactAttribute_WithInvalidMaxRetries_DefaultsToThree()
    {
        // Arrange & Act
        var attribute = new RetryFactAttribute(0);

        // Assert
        Assert.Equal(3, attribute.MaxRetries);
    }

    [Fact]
    public void RetryTheoryAttribute_DefaultMaxRetries_IsThree()
    {
        // Arrange & Act
        var attribute = new RetryTheoryAttribute();

        // Assert
        Assert.Equal(3, attribute.MaxRetries);
    }

    [Fact]
    public void RetryTheoryAttribute_WithCustomMaxRetries_SetsValue()
    {
        // Arrange & Act
        var attribute = new RetryTheoryAttribute(5);

        // Assert
        Assert.Equal(5, attribute.MaxRetries);
    }

    [Fact]
    public void RetryTheoryAttribute_WithInvalidMaxRetries_DefaultsToThree()
    {
        // Arrange & Act
        var attribute = new RetryTheoryAttribute(0);

        // Assert
        Assert.Equal(3, attribute.MaxRetries);
    }

    /// <summary>
    /// This test fails on the first attempt but succeeds on the second.
    /// The retry mechanism should make this test pass overall.
    /// </summary>
    [RetryFact]
    public void RetryFact_SucceedsOnSecondAttempt()
    {
        _retryFactSucceedsOnSecondAttemptCounter++;
        _logger.LogInformation("RetryFact_SucceedsOnSecondAttempt attempt {Attempt}", _retryFactSucceedsOnSecondAttemptCounter);

        if (_retryFactSucceedsOnSecondAttemptCounter < 2)
        {
            Assert.Fail($"Intentional failure on attempt {_retryFactSucceedsOnSecondAttemptCounter}");
        }

        Assert.True(true, "Succeeded on retry");
    }

    /// <summary>
    /// This test fails on the first two attempts but succeeds on the third.
    /// The retry mechanism should make this test pass overall.
    /// </summary>
    [RetryFact]
    public void RetryFact_SucceedsOnThirdAttempt()
    {
        _retryFactSucceedsOnThirdAttemptCounter++;
        _logger.LogInformation("RetryFact_SucceedsOnThirdAttempt attempt {Attempt}", _retryFactSucceedsOnThirdAttemptCounter);

        if (_retryFactSucceedsOnThirdAttemptCounter < 3)
        {
            Assert.Fail($"Intentional failure on attempt {_retryFactSucceedsOnThirdAttemptCounter}");
        }

        Assert.True(true, "Succeeded on third attempt");
    }

    /// <summary>
    /// This test verifies that custom max retries are respected.
    /// The test fails on attempts 1-4 but succeeds on attempt 5.
    /// With maxRetries=5, it should pass.
    /// </summary>
    [RetryFact(5)]
    public void RetryFact_WithCustomMaxRetries_RespectsValue()
    {
        _retryFactWithCustomRetriesCounter++;
        _logger.LogInformation("RetryFact_WithCustomMaxRetries_RespectsValue attempt {Attempt}", _retryFactWithCustomRetriesCounter);

        if (_retryFactWithCustomRetriesCounter < 5)
        {
            Assert.Fail($"Intentional failure on attempt {_retryFactWithCustomRetriesCounter}");
        }

        Assert.True(true, "Succeeded on fifth attempt with custom max retries");
    }

    /// <summary>
    /// This test succeeds on the first attempt.
    /// The retry mechanism should not interfere with passing tests.
    /// </summary>
    [RetryFact]
    public void RetryFact_SucceedsOnFirstAttempt()
    {
        Assert.True(true, "Succeeded on first attempt");
    }

    /// <summary>
    /// This theory test fails on the first attempt but succeeds on the second.
    /// </summary>
    [RetryTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void RetryTheory_SucceedsOnSecondAttempt(int value)
    {
        _retryTheorySucceedsOnSecondAttemptCounter++;
        _logger.LogInformation("RetryTheory_SucceedsOnSecondAttempt attempt {Attempt} with value {Value}",
            _retryTheorySucceedsOnSecondAttemptCounter, value);

        // Only fail on the very first call (not per-parameter)
        if (_retryTheorySucceedsOnSecondAttemptCounter == 1)
        {
            Assert.Fail($"Intentional failure on first attempt");
        }

        Assert.Equal(value, value);
    }

    /// <summary>
    /// This theory test succeeds on the first attempt.
    /// </summary>
    [RetryTheory]
    [InlineData("a")]
    [InlineData("b")]
    public void RetryTheory_SucceedsOnFirstAttempt(string value)
    {
        Assert.NotNull(value);
    }
}

