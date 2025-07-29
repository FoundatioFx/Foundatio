using Foundatio.Redis.Utility;
using Xunit;

namespace Foundatio.Redis.Tests.Utility;

public class RedisPatternTests
{
    [Fact]
    public void Escape_WithNullInput_ReturnsNull()
    {
        // Arrange
        string input = null;

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Escape_WithEmptyString_ReturnsEmptyString()
    {
        // Arrange
        string input = "";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void Escape_WithNormalString_ReturnsUnchanged()
    {
        // Arrange
        string input = "normalstring123";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("normalstring123", result);
    }

    [Fact]
    public void Escape_WithAsterisk_EscapesAsterisk()
    {
        // Arrange
        string input = "prefix*suffix";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("prefix\\*suffix", result);
    }

    [Fact]
    public void Escape_WithQuestionMark_EscapesQuestionMark()
    {
        // Arrange
        string input = "prefix?suffix";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("prefix\\?suffix", result);
    }

    [Fact]
    public void Escape_WithSquareBrackets_EscapesBrackets()
    {
        // Arrange
        string input = "prefix[abc]suffix";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("prefix\\[abc\\]suffix", result);
    }

    [Fact]
    public void Escape_WithBackslash_PreservesBackslash()
    {
        // Arrange
        string input = "prefix\\suffix";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("prefix\\\\suffix", result);
    }

    [Fact]
    public void Escape_WithAlreadyEscapedAsterisk_PreservesEscaping()
    {
        // Arrange
        string input = "prefix\\*suffix";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("prefix\\\\\\*suffix", result);
    }

    [Fact]
    public void Escape_WithAlreadyEscapedQuestionMark_PreservesEscaping()
    {
        // Arrange
        string input = "prefix\\?suffix";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("prefix\\\\\\?suffix", result);
    }

    [Fact]
    public void Escape_WithAlreadyEscapedBrackets_PreservesEscaping()
    {
        // Arrange
        string input = "prefix\\[abc\\]suffix";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("prefix\\\\\\[abc\\\\\\]suffix", result);
    }

    [Fact]
    public void Escape_WithMultipleMetaCharacters_EscapesAll()
    {
        // Arrange
        string input = "test*?[abc]";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("test\\*\\?\\[abc\\]", result);
    }

    [Fact]
    public void Escape_WithMixedEscapedAndUnescapedCharacters_HandlesCorrectly()
    {
        // Arrange
        string input = "test\\**?[abc]";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("test\\\\\\*\\*\\?\\[abc\\]", result);
    }

    [Fact]
    public void Escape_WithDoubleBackslash_PreservesDoubleBackslash()
    {
        // Arrange
        string input = "test\\\\*";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("test\\\\\\\\\\*", result);
    }

    [Fact]
    public void Escape_WithBackslashAtEnd_PreservesBackslash()
    {
        // Arrange
        string input = "test\\";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("test\\\\", result);
    }

    [Fact]
    public void Escape_WithBackslashFollowedByNormalChar_PreservesBackslash()
    {
        // Arrange
        string input = "test\\a";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("test\\\\a", result);
    }

    [Fact]
    public void Escape_WithConsecutiveMetaCharacters_EscapesAll()
    {
        // Arrange
        string input = "**??[[]]";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("\\*\\*\\?\\?\\[\\[\\]\\]", result);
    }

    [Fact]
    public void Escape_WithOnlyMetaCharacters_EscapesAll()
    {
        // Arrange
        string input = "*?[]";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("\\*\\?\\[\\]", result);
    }

    [Fact]
    public void Escape_WithComplexPattern_HandlesCorrectly()
    {
        // Arrange
        string input = "user:*:session[?]\\already\\*escaped";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("user:\\*:session\\[\\?\\]\\\\already\\\\\\*escaped", result);
    }

    [Fact]
    public void Escape_WithRealWorldRedisKey_EscapesCorrectly()
    {
        // Arrange
        string input = "cache:user[123]:data*";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("cache:user\\[123\\]:data\\*", result);
    }

    [Theory]
    [InlineData("*", "\\*")]
    [InlineData("?", "\\?")]
    [InlineData("[", "\\[")]
    [InlineData("]", "\\]")]
    [InlineData("\\", "\\\\")]
    [InlineData("test", "test")]
    public void Escape_WithSingleCharacterInputs_HandlesCorrectly(string input, string expected)
    {
        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Escape_WithBackslashFollowedByMetaChar_HandlesCorrectly()
    {
        // Arrange
        string input = "\\*";

        // Act
        string result = RedisPattern.Escape(input);

        // Assert
        Assert.Equal("\\\\\\*", result);
    }
}
