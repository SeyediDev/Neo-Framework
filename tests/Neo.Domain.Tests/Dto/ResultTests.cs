using FluentAssertions;
using Neo.Domain.Dto;

namespace Neo.Domain.Tests.Dto;

public class ResultTests
{
    [Fact]
    public void Success_ShouldCreateSuccessfulResult()
    {
        // Act
        var result = Result.Success();

        // Assert
        result.Succeeded.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public void Failure_WithSingleError_ShouldCreateFailedResult()
    {
        // Arrange
        var error = "Error message";

        // Act
        var result = Result.Failure(error);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].Should().Be(error);
        result.ErrorMessage.Should().Be(error);
    }

    [Fact]
    public void Failure_WithMultipleErrors_ShouldCreateFailedResult()
    {
        // Arrange
        var errors = new[] { "Error 1", "Error 2", "Error 3" };

        // Act
        var result = Result.Failure(errors);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().HaveCount(3);
        result.Errors.Should().BeEquivalentTo(errors);
        result.ErrorMessage.Should().Be("Error 1, Error 2, Error 3");
    }

    [Fact]
    public void Failure_WithEmptyErrors_ShouldCreateFailedResult()
    {
        // Arrange
        var errors = Array.Empty<string>();

        // Act
        var result = Result.Failure(errors);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().BeEmpty();
        result.ErrorMessage.Should().BeEmpty();
    }
}

public class ResultTTests
{
    [Fact]
    public void Success_WithData_ShouldCreateSuccessfulResult()
    {
        // Arrange
        var data = "Test Data";

        // Act
        var result = Result<string>.Success(data);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(data);
        result.Errors.Should().BeEmpty();
        result.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public void Failure_WithSingleError_ShouldCreateFailedResult()
    {
        // Arrange
        var error = "Error message";

        // Act
        var result = Result<string>.Failure(error);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
        result.Data.Should().BeNull();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].Should().Be(error);
        result.ErrorMessage.Should().Be(error);
    }

    [Fact]
    public void Failure_WithMultipleErrors_ShouldCreateFailedResult()
    {
        // Arrange
        var errors = new[] { "Error 1", "Error 2" };

        // Act
        var result = Result<int>.Failure(errors);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
        result.Data.Should().Be(0); // default int
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().BeEquivalentTo(errors);
        result.ErrorMessage.Should().Be("Error 1, Error 2");
    }

    [Fact]
    public void Success_WithNullData_ShouldCreateSuccessfulResult()
    {
        // Arrange
        string? data = null;

        // Act
        var result = Result<string?>.Success(data);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeNull();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Success_WithComplexObject_ShouldCreateSuccessfulResult()
    {
        // Arrange
        var data = new { Id = 1, Name = "Test" };

        // Act
        var result = Result<object>.Success(data);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Data.Should().Be(data);
    }
}

