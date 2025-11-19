using FluentAssertions;
using Neo.Common.Extensions;

namespace Neo.Common.Tests.Extensions;

public class DateTimeExtensionsTests
{
    [Fact]
    public void ToPersianDate_ShouldConvertCorrectly()
    {
        // Arrange - March 21, 2024 (Gregorian) = 1403/01/01 (Persian)
        var dateTime = new DateTime(2024, 3, 21);

        // Act
        var result = dateTime.ToPersianDate();

        // Assert
        result.Should().MatchRegex(@"^\d{4}/\d{2}/\d{2}$");
        result.Should().StartWith("1403");
    }

    [Fact]
    public void ToPersianDateStr_ShouldReturn8CharacterString()
    {
        // Arrange
        var dateTime = new DateTime(2024, 3, 21);

        // Act
        var result = dateTime.ToPersianDateStr();

        // Assert
        result.Should().HaveLength(8);
        result.Should().MatchRegex(@"^\d{8}$");
    }

    [Fact]
    public void ToPersianDateInt_ShouldReturnInteger()
    {
        // Arrange
        var dateTime = new DateTime(2024, 3, 21);

        // Act
        var result = dateTime.ToPersianDateInt();

        // Assert
        result.Should().BeGreaterThan(0);
        result.Should().BeLessThan(99999999);
    }

    [Fact]
    public void ToPersianDate_WithDifferentDates_ShouldFormatCorrectly()
    {
        // Arrange
        var dateTime1 = new DateTime(2023, 1, 1);
        var dateTime2 = new DateTime(2024, 12, 31);

        // Act
        var result1 = dateTime1.ToPersianDate();
        var result2 = dateTime2.ToPersianDate();

        // Assert
        result1.Should().MatchRegex(@"^\d{4}/\d{2}/\d{2}$");
        result2.Should().MatchRegex(@"^\d{4}/\d{2}/\d{2}$");
    }
}

