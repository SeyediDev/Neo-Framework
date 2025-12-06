using FluentAssertions;
using Neo.Common.Extensions;

namespace Neo.Common.Tests.Extensions;

public class TypeExtensionsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData("", true)]
    [InlineData("test", false)]
    public void IsNullOrDefault_ShouldReturnCorrectValue<T>(T? argument, bool expected)
    {
        // Act
        var result = argument.IsNullOrDefault();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void HasValue_ShouldReturnOppositeOfIsNullOrDefault()
    {
        // Arrange
        var value = 5;

        // Act
        var result = value.HasValue();

        // Assert
        result.Should().BeTrue();
        value.IsNullOrDefault().Should().BeFalse();
    }

    [Fact]
    public void IfType_WithMatchingType_ShouldExecuteAction()
    {
        // Arrange
        object item = "test";
        var executed = false;

        // Act
        item.IfType<string>(s => executed = true);

        // Assert
        executed.Should().BeTrue();
    }

    [Fact]
    public void IfType_WithNonMatchingType_ShouldNotExecuteAction()
    {
        // Arrange
        object item = 123;
        var executed = false;

        // Act
        item.IfType<string>(s => executed = true);

        // Assert
        executed.Should().BeFalse();
    }

    [Fact]
    public void As_WithValidCast_ShouldReturnCastedValue()
    {
        // Arrange
        object obj = "test";

        // Act
        var result = Neo.Common.Extensions.TypeExtensions.As<string>(obj);

        // Assert
        result.Should().Be("test");
    }

    [Fact]
    public void ToJson_WithValidObject_ShouldSerialize()
    {
        // Arrange
        var obj = new { Name = "Test", Age = 30 };

        // Act
        var result = obj.ToJson();

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Test");
    }

    [Fact]
    public void ToJson_WithNull_ShouldReturnNull()
    {
        // Arrange
        object? obj = null;

        // Act
        var result = obj.ToJson();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void FromJson_WithValidJson_ShouldDeserialize()
    {
        // Arrange
        var json = """{"name":"Test","age":30}""";

        // Act
        var result = json.FromJson<TestClass>();

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Test");
        result.Age.Should().Be(30);
    }

    [Fact]
    public void FromJson_WithEmptyString_ShouldReturnDefault()
    {
        // Arrange
        var json = "";

        // Act
        var result = json.FromJson<TestClass>();

        // Assert
        result.Should().BeNull();
    }

    public class TestClass
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }
}

