using FluentAssertions;
using Neo.Common.Extensions;

namespace Neo.Common.Tests.Extensions;

public class DictionaryExtensionsTests
{
    public class TestClass
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    [Fact]
    public void ToObject_WithValidDictionary_ShouldCreateObject()
    {
        // Arrange
        var dictionary = new Dictionary<string, string>
        {
            { "Name", "John" },
            { "Age", "30" }
        };

        // Act
        var result = dictionary.ToObject<TestClass>();

        // Assert
        result.Name.Should().Be("John");
        result.Age.Should().Be(30);
    }

    [Fact]
    public void ToObject_WithEmptyDictionary_ShouldCreateEmptyObject()
    {
        // Arrange
        var dictionary = new Dictionary<string, string>();

        // Act
        var result = dictionary.ToObject<TestClass>();

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("");
        result.Age.Should().Be(0);
    }

    [Fact]
    public void MergeObject_WithValidObject_ShouldMergeProperties()
    {
        // Arrange
        var dictionary = new Dictionary<string, object>();
        var obj = new TestClass { Name = "John", Age = 30 };

        // Act
        dictionary.MergeObject(obj);

        // Assert
        dictionary.Should().ContainKey("Name");
        dictionary.Should().ContainKey("Age");
        dictionary["Name"].Should().Be("John");
        dictionary["Age"].Should().Be(30);
    }

    [Fact]
    public void MergeObject_WithNullObject_ShouldNotAddAnything()
    {
        // Arrange
        var dictionary = new Dictionary<string, object>();
        TestClass? obj = null;

        // Act
        dictionary.MergeObject(obj!);

        // Assert
        dictionary.Should().BeEmpty();
    }

    [Fact]
    public void GetOrDefault_WithExistingKey_ShouldReturnValue()
    {
        // Arrange
        var dictionary = new Dictionary<string, int> { { "key1", 100 } };

        // Act
        var result = dictionary.GetOrDefault("key1");

        // Assert
        result.Should().Be(100);
    }

    [Fact]
    public void GetOrDefault_WithNonExistingKey_ShouldReturnDefault()
    {
        // Arrange
        var dictionary = new Dictionary<string, int>();

        // Act
        var result = dictionary.GetOrDefault("key1");

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void GetOrAdd_WithExistingKey_ShouldReturnExistingValue()
    {
        // Arrange
        var dictionary = new Dictionary<string, int> { { "key1", 100 } };

        // Act
        var result = dictionary.GetOrAdd("key1", k => 200);

        // Assert
        result.Should().Be(100);
        dictionary["key1"].Should().Be(100);
    }

    [Fact]
    public void GetOrAdd_WithNonExistingKey_ShouldAddAndReturnNewValue()
    {
        // Arrange
        var dictionary = new Dictionary<string, int>();

        // Act
        var result = dictionary.GetOrAdd("key1", k => 200);

        // Assert
        result.Should().Be(200);
        dictionary["key1"].Should().Be(200);
    }

    [Fact]
    public void GetOrAdd_WithFactory_ShouldUseFactory()
    {
        // Arrange
        var dictionary = new Dictionary<string, int>();

        // Act
        var result = dictionary.GetOrAdd("key1", () => 300);

        // Assert
        result.Should().Be(300);
        dictionary["key1"].Should().Be(300);
    }
}

