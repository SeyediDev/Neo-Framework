using FluentAssertions;
using Neo.Common.Extensions;

namespace Neo.Common.Tests.Extensions;

public class GenericExtensionsTests
{
    [Theory]
    [InlineData(1, new[] { 1, 2, 3 }, true)]
    [InlineData(4, new[] { 1, 2, 3 }, false)]
    [InlineData("test", new[] { "test", "value" }, true)]
    public void In_WithParams_ShouldReturnCorrectValue<T>(T item, T[] items, bool expected)
    {
        // Act
        var result = item.In(items);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void In_WithNullParams_ShouldThrow()
    {
        // Arrange
        var item = 1;
        int[]? items = null;

        // Act
        var act = () => item.In(items!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(1, new[] { 1, 2, 3 }, true)]
    [InlineData(4, new[] { 1, 2, 3 }, false)]
    public void In_WithEnumerable_ShouldReturnCorrectValue(int item, IEnumerable<int> items, bool expected)
    {
        // Act
        var result = item.In(items);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void In_WithNullEnumerable_ShouldThrow()
    {
        // Arrange
        var item = 1;
        IEnumerable<int>? items = null;

        // Act
        var act = () => item.In(items!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3 }, true)]
    [InlineData(new int[0], false)]
    public void NotNullNorEmpty_ShouldReturnCorrectValue(int[] source, bool expected)
    {
        // Act
        var result = source.NotNullNorEmpty();

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Hello", 3, "Hel")]
    [InlineData("Hello", 10, "Hello")]
    [InlineData("Hello", 0, "")]
    public void Limit_ShouldTruncateString(string str, int limit, string expected)
    {
        // Act
        var result = str.Limit(limit);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Try_WithNoException_ShouldExecuteAction()
    {
        // Arrange
        var item = 1;
        var executed = false;

        // Act
        item.Try(() => executed = true);

        // Assert
        executed.Should().BeTrue();
    }

    [Fact]
    public void Try_WithException_ShouldCatchException()
    {
        // Arrange
        var item = 1;

        // Act
        var act = () => item.Try(() => throw new Exception("Test exception"));

        // Assert
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0L, true)]
    [InlineData(1L, false)]
    public void IsEmpty_ShouldReturnCorrectValue(long? value, bool expected)
    {
        // Act
        var result = value.IsEmpty();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Yield_ShouldReturnSingleItem()
    {
        // Arrange
        var item = "test";

        // Act
        var result = item.Yield().ToList();

        // Assert
        result.Should().HaveCount(1);
        result.First().Should().Be("test");
    }

    [Fact]
    public void TryGetValue_WithExistingKey_ShouldReturnTrue()
    {
        // Arrange
        var dictionary = new Dictionary<string, int> { { "key1", 100 } };
        var key = "key1";

        // Act
        var result = GenericExtensions.TryGetValue(dictionary, ref key, () => "default", out var value);

        // Assert
        result.Should().BeTrue();
        value.Should().Be(100);
        key.Should().Be("key1");
    }

    [Fact]
    public void NotNullNorEmpty_WithNull_ShouldReturnFalse()
    {
        // Arrange
        int[]? source = null;

        // Act
        var result = source!.NotNullNorEmpty();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetValue_WithNullKey_ShouldUseReplacement()
    {
        // Arrange
        var dictionary = new Dictionary<string, int> { { "default", 200 } };
        string? key = null;

        // Act
        var result = GenericExtensions.TryGetValue(dictionary, ref key!, () => "default", out var value);

        // Assert
        result.Should().BeTrue();
        value.Should().Be(200);
        key.Should().Be("default");
    }
}

