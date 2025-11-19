using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Neo.Common.Attributes;
using Neo.Common.Extensions;

namespace Neo.Common.Tests.Extensions;

public enum TestEnum
{
    [Description("Test Description")]
    Value1,
    
    [EnumDescription("Persian Name", "EnglishName")]
    Value2,
    
    [Display(Name = "Display Name")]
    Value3,
    
    [Title("Title Value")]
    Value4,
    
    Value5
}

public class EnumExtensionsTests
{
    [Fact]
    public void GetAttribute_WhenAttributeExists_ShouldReturnAttribute()
    {
        // Arrange
        var enumValue = TestEnum.Value1;

        // Act
        var result = enumValue.GetAttribute<DescriptionAttribute>();

        // Assert
        result.Should().NotBeNull();
        result!.Description.Should().Be("Test Description");
    }

    [Fact]
    public void GetAttribute_WhenAttributeDoesNotExist_ShouldReturnNull()
    {
        // Arrange
        var enumValue = TestEnum.Value5;

        // Act
        var result = enumValue.GetAttribute<DescriptionAttribute>();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ToName_WithDescriptionAttribute_ShouldReturnDescription()
    {
        // Arrange
        var enumValue = TestEnum.Value1;

        // Act
        var result = enumValue.ToName();

        // Assert
        result.Should().Be("Test Description");
    }

    [Fact]
    public void ToName_WithEnumDescriptionAttribute_ShouldReturnName()
    {
        // Arrange
        var enumValue = TestEnum.Value2;

        // Act
        var result = enumValue.ToName();

        // Assert
        result.Should().Be("Persian Name");
    }

    [Fact]
    public void ToName_WithDisplayAttribute_ShouldReturnDisplayName()
    {
        // Arrange
        var enumValue = TestEnum.Value3;

        // Act
        var result = enumValue.ToName();

        // Assert
        result.Should().Be("Display Name");
    }

    [Fact]
    public void ToName_WithoutAttribute_ShouldReturnToString()
    {
        // Arrange
        var enumValue = TestEnum.Value5;

        // Act
        var result = enumValue.ToName();

        // Assert
        result.Should().Be("Value5");
    }

    [Fact]
    public void GetTitle_WithTitleAttribute_ShouldReturnTitle()
    {
        // Arrange
        var enumValue = TestEnum.Value4;

        // Act
        var result = enumValue.GetTitle();

        // Assert
        result.Should().Be("Title Value");
    }

    [Fact]
    public void GetTitle_WithoutTitleAttribute_ShouldReturnToString()
    {
        // Arrange
        var enumValue = TestEnum.Value5;

        // Act
        var result = enumValue.GetTitle();

        // Assert
        result.Should().Be("Value5");
    }

    [Fact]
    public void ToInt_ShouldConvertToInteger()
    {
        // Arrange
        var enumValue = TestEnum.Value2;

        // Act
        var result = enumValue.ToInt();

        // Assert
        result.Should().Be(1);
    }

    [Theory]
    [InlineData(TestEnum.Value1, TestEnum.Value1)]
    [InlineData("Value2", TestEnum.Value2)]
    [InlineData("value3", TestEnum.Value3)] // case insensitive
    [InlineData(1, TestEnum.Value2)]
    [InlineData("Invalid", TestEnum.Value1)] // should return default
    public void ToEnum_ShouldConvertCorrectly(object input, TestEnum expected)
    {
        // Arrange
        var defaultValue = TestEnum.Value1;

        // Act
        var result = input.ToEnum(defaultValue);

        // Assert
        result.Should().Be(expected);
    }
}

