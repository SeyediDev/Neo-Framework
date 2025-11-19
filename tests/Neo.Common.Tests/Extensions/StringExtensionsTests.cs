using FluentAssertions;
using Neo.Common.Extensions;

namespace Neo.Common.Tests.Extensions;

public class StringExtensionsTests
{
    [Theory]
    [InlineData("HelloWorld", "hello-world")]
    [InlineData("HelloWorldTest", "hello-world-test")]
    [InlineData("XML", "xml")]
    [InlineData("XMLHttpRequest", "xml-http-request")]
    [InlineData("", "")]
    public void ToKebabCase_ShouldConvertCorrectly(string input, string expected)
    {
        // Act
        var result = input.ToKebabCase();

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("hello world", true, "HW")]
    [InlineData("hello world", false, "HW")]
    [InlineData("test,case;example", true, "TCE")]
    [InlineData("test'case", true, "TC")]
    public void ToPascalCase_WithLower_ShouldConvertCorrectly(string input, bool lower, string expected)
    {
        // Act
        var result = input.ToPascalCase(lower);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void ForceMax_WhenStringExceedsMax_ShouldTruncate()
    {
        // Arrange
        var input = "This is a long string";
        var max = 10;

        // Act
        var result = input.ForceMax(max);

        // Assert
        result.Should().Be("This is a ");
        result.Length.Should().Be(max);
    }

    [Fact]
    public void ForceMax_WhenStringShorterThanMax_ShouldReturnOriginal()
    {
        // Arrange
        var input = "Short";
        var max = 10;

        // Act
        var result = input.ForceMax(max);

        // Assert
        result.Should().Be(input);
    }

    [Fact]
    public void ForceMax_WhenStringIsNull_ShouldReturnNull()
    {
        // Arrange
        string? input = null;
        var max = 10;

        // Act
        var result = input!.ForceMax(max);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("test123", "^test", true)]
    [InlineData("test123", "^abc", false)]
    [InlineData("hello", "^h", true)]
    [InlineData("hello", "^H", false)]
    public void RegexStartsWith_ShouldMatchCorrectly(string input, string pattern, bool expected)
    {
        // Act
        var result = input.RegexStartsWith(pattern);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Remove_ShouldRemoveSubstring()
    {
        // Arrange
        var input = "Hello World";
        var substring = "World";

        // Act
        var result = input.Remove(substring);

        // Assert
        result.Should().Be("Hello ");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("test", false)]
    public void IsNullOrEmpty_ShouldReturnCorrectValue(string input, bool expected)
    {
        // Act
        var result = input.IsNullOrEmpty();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void IsNullOrEmpty_WithNull_ShouldReturnTrue()
    {
        // Arrange
        string? input = null;

        // Act
        var result = input!.IsNullOrEmpty();

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("test", true)]
    public void HasValue_ShouldReturnCorrectValue(string input, bool expected)
    {
        // Act
        var result = input.HasValue();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void HasValue_WithNull_ShouldReturnFalse()
    {
        // Arrange
        string? input = null;

        // Act
        var result = input!.HasValue();

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("test", false)]
    public void HasNotValue_ShouldReturnCorrectValue(string input, bool expected)
    {
        // Act
        var result = input.HasNotValue();

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Hello", "hello", true)]
    [InlineData("Hello", "HELLO", true)]
    [InlineData("  Hello  ", "hello", true)]
    [InlineData("Hello", "World", false)]
    public void IsEqualCaseInsensitive_ShouldCompareCorrectly(string value, string compareTo, bool expected)
    {
        // Act
        var result = value.IsEqualCaseInsensitive(compareTo);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("a.b.c", ".", 1, "a")]
    [InlineData("a.b.c", ".", 2, "b")]
    [InlineData("a.b.c", ".", 3, "c")]
    [InlineData("a.b.c", ".", 4, "")]
    [InlineData("test", ".", 1, "")]
    public void Slice_ShouldReturnCorrectPart(string value, string splitter, int partNumber, string expected)
    {
        // Act
        var result = value.Slice(splitter, partNumber);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void HasNotValue_WithNull_ShouldReturnTrue()
    {
        // Arrange
        string? input = null;

        // Act
        var result = input!.HasNotValue();

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("09123456789", false, "091234xxx89")]
    [InlineData("09123456789", true, "89****091234")]
    [InlineData("123", false, "")]
    public void MaskMobileNumber_ShouldMaskCorrectly(string input, bool inverse, string expected)
    {
        // Act
        var result = input.MaskMobileNumber(inverse);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void ToCamelCase_ShouldConvertFirstLetterToLower()
    {
        // Arrange
        var input = "HelloWorld";

        // Act
        var result = input.ToCamelCase();

        // Assert
        result.Should().Be("helloWorld");
    }

    [Fact]
    public void ToPascalCase_ShouldConvertFirstLetterToUpper()
    {
        // Arrange
        var input = "helloWorld";

        // Act
        var result = input.ToPascalCase();

        // Assert
        result.Should().Be("HelloWorld");
    }

    [Theory]
    [InlineData("test123", true)]
    [InlineData("test_123", true)]
    [InlineData("test-123", true)]
    [InlineData("test.123", true)]
    [InlineData("test@123", false)]
    [InlineData("test#123", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("test فارسی", true)]
    public void IsSafeMultiCultureString_ShouldValidateCorrectly(string input, bool expected)
    {
        // Act
        var result = input.IsSafeMultiCultureString();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void ToBase64Encode_ShouldEncodeCorrectly()
    {
        // Arrange
        var input = "Hello World";

        // Act
        var result = input.ToBase64Encode();

        // Assert
        result.Should().Be("SGVsbG8gV29ybGQ=");
    }

    [Fact]
    public void MaskMobileNumber_WithNull_ShouldReturnEmpty()
    {
        // Arrange
        string? input = null;

        // Act
        var result = input!.MaskMobileNumber(false);

        // Assert
        result.Should().Be("");
    }

    [Theory]
    [InlineData(123, 123)]
    [InlineData(123.5, 123)]
    [InlineData(123L, 123)]
    [InlineData("123", 123)]
    [InlineData("123.5", 123)]
    [InlineData("invalid", 0)]
    public void Int_ShouldConvertCorrectly(object value, int expected)
    {
        // Act
        var result = value.Int();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Int_WithNull_ShouldReturnZero()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value!.Int();

        // Assert
        result.Should().Be(0);
    }

    [Theory]
    [InlineData(123, 123L)]
    [InlineData(123.5, 123L)]
    [InlineData(123L, 123L)]
    [InlineData("123", 123L)]
    [InlineData("invalid", 0L)]
    public void Long_ShouldConvertCorrectly(object value, long expected)
    {
        // Act
        var result = value.Long();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Sha256_ShouldHashCorrectly()
    {
        // Arrange
        var input = "test";

        // Act
        var result = input.Sha256();

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Length.Should().Be(64); // SHA256 produces 64 character hex string
        result.Should().Be("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("invalid", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void ToBoolean_ShouldConvertCorrectly(string? value, bool expected)
    {
        // Act
        var result = value.ToBoolean();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Long_WithNull_ShouldReturnZero()
    {
        // Arrange
        object? value = null;

        // Act
        var result = value!.Long();

        // Assert
        result.Should().Be(0L);
    }

    [Fact]
    public void ToKebabCase_WithNull_ShouldReturnNull()
    {
        // Arrange
        string? input = null;

        // Act
        var result = input!.ToKebabCase();

        // Assert
        result.Should().BeNull();
    }
}

