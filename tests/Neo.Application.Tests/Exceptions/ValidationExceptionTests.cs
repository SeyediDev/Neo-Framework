using FluentAssertions;
using FluentValidation.Results;
using Neo.Application.Exceptions;

namespace Neo.Application.Tests.Exceptions;

public class ValidationExceptionTests
{
    [Fact]
    public void Constructor_WithoutParameters_ShouldInitializeEmptyErrors()
    {
        // Act
        var exception = new ValidationException();

        // Assert
        exception.Errors.Should().BeEmpty();
        exception.Message.Should().Be("One or more validation failures have occurred.");
    }

    [Fact]
    public void Constructor_WithValidationFailures_ShouldGroupErrorsByPropertyName()
    {
        // Arrange
        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required"),
            new("Name", "Name must be at least 3 characters"),
            new("Email", "Email is invalid")
        };

        // Act
        var exception = new ValidationException(failures);

        // Assert
        exception.Errors.Should().HaveCount(2);
        exception.Errors["Name"].Should().HaveCount(2);
        exception.Errors["Email"].Should().HaveCount(1);
        exception.Errors["Name"].Should().Contain("Name is required");
        exception.Errors["Name"].Should().Contain("Name must be at least 3 characters");
        exception.Errors["Email"].Should().Contain("Email is invalid");
    }

    [Fact]
    public void Constructor_WithEmptyFailures_ShouldInitializeEmptyErrors()
    {
        // Arrange
        var failures = new List<ValidationFailure>();

        // Act
        var exception = new ValidationException(failures);

        // Assert
        exception.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithNullFailures_ShouldThrow()
    {
        // Arrange
        IEnumerable<ValidationFailure>? failures = null;

        // Act
        var act = () => new ValidationException(failures!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }
}

