using FluentAssertions;
using Neo.Common.Utility;

namespace Neo.Common.Tests.Utility;

public class Base64FileValidatorTests
{
    [Fact]
    public void ValidateBase64File_WithValidPngBase64_ShouldReturnValid()
    {
        // Arrange - Valid PNG base64 (1x1 transparent PNG)
        var base64String = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

        // Act
        var result = Base64FileValidator.ValidateBase64File(base64String);

        // Assert
        result.IsValid.Should().BeTrue();
        result.fileBytes.Should().NotBeNull();
        result.mimeType.Should().Be("image/png");
        result.ErrorMessage.Should().Be("Valid file");
    }

    [Fact]
    public void ValidateBase64File_WithEmptyString_ShouldReturnInvalid()
    {
        // Arrange
        var base64String = "";

        // Act
        var result = Base64FileValidator.ValidateBase64File(base64String);

        // Assert
        result.IsValid.Should().BeFalse();
        result.fileBytes.Should().BeNull();
        result.mimeType.Should().BeNull();
        result.ErrorMessage.Should().Be("Input is empty");
    }

    [Fact]
    public void ValidateBase64File_WithNullString_ShouldReturnInvalid()
    {
        // Arrange
        string? base64String = null;

        // Act
        var result = Base64FileValidator.ValidateBase64File(base64String!);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Input is empty");
    }

    [Fact]
    public void ValidateBase64File_WithInvalidBase64_ShouldReturnInvalid()
    {
        // Arrange
        var base64String = "This is not base64!@#$%";

        // Act
        var result = Base64FileValidator.ValidateBase64File(base64String);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid Base64 format");
    }

    [Fact]
    public void ValidateBase64File_WithFileExceedingMaxSize_ShouldReturnInvalid()
    {
        // Arrange - Create a large base64 string (larger than 5MB)
        var largeData = new byte[6 * 1024 * 1024]; // 6MB
        Array.Fill(largeData, (byte)65); // Fill with 'A'
        var base64String = Convert.ToBase64String(largeData);

        // Act
        var result = Base64FileValidator.ValidateBase64File(base64String, maxSizeInMegabyte: 5);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("File too large");
    }

    [Fact]
    public void ValidateBase64File_WithValidJpegBase64_ShouldReturnValid()
    {
        // Arrange - Minimal valid JPEG (just header)
        var jpegHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
        var base64String = Convert.ToBase64String(jpegHeader);

        // Act
        var result = Base64FileValidator.ValidateBase64File(base64String);

        // Assert
        result.IsValid.Should().BeTrue();
        result.mimeType.Should().Be("image/jpeg");
    }

    [Fact]
    public void ValidateBase64File_WithUnsupportedFileType_ShouldReturnInvalid()
    {
        // Arrange - Create base64 that doesn't match any known type
        var unknownData = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
        var base64String = Convert.ToBase64String(unknownData);

        // Act
        var result = Base64FileValidator.ValidateBase64File(base64String);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Unsupported file type");
    }

    [Fact]
    public void ValidateBase64File_WithCustomMaxSize_ShouldRespectLimit()
    {
        // Arrange
        var data = new byte[2 * 1024 * 1024]; // 2MB
        Array.Fill(data, (byte)65);
        var base64String = Convert.ToBase64String(data);

        // Act
        var result = Base64FileValidator.ValidateBase64File(base64String, maxSizeInMegabyte: 1);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("File too large");
    }

    [Fact]
    public void ValidateBase64File_WithValidPdfBase64_ShouldReturnValid()
    {
        // Arrange - Minimal PDF header
        var pdfHeader = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 };
        var base64String = Convert.ToBase64String(pdfHeader);

        // Act
        var result = Base64FileValidator.ValidateBase64File(base64String);

        // Assert
        result.IsValid.Should().BeTrue();
        result.mimeType.Should().Be("application/pdf");
    }
}

