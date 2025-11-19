using FluentAssertions;
using Neo.Application.Exceptions;

namespace Neo.Application.Tests.Exceptions;

public class BadRequestExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_ShouldSetMessage()
    {
        // Arrange
        var message = "Bad request error";

        // Act
        var exception = new BadRequestException(message);

        // Assert
        exception.Message.Should().Be(message);
        exception.Should().BeAssignableTo<Exception>();
    }

    [Fact]
    public void Constructor_WithEmptyMessage_ShouldSetEmptyMessage()
    {
        // Arrange
        var message = "";

        // Act
        var exception = new BadRequestException(message);

        // Assert
        exception.Message.Should().Be(message);
    }
}

