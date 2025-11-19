using FluentAssertions;
using Neo.Application.Models;

namespace Neo.Application.Tests.Models;

public class PaginationExtensionsTests
{
    [Fact]
    public void Pagination_WithValidInput_ShouldReturnCorrectPage()
    {
        // Arrange
        var list = Enumerable.Range(1, 100).ToList();
        var pageNumber = 2;
        var pageSize = 10;

        // Act
        var result = list.Pagination(pageNumber, pageSize).ToList();

        // Assert
        result.Should().HaveCount(10);
        result.First().Should().Be(11);
        result.Last().Should().Be(20);
    }

    [Fact]
    public void Pagination_WithFirstPage_ShouldReturnFirstItems()
    {
        // Arrange
        var list = Enumerable.Range(1, 50).ToList();

        // Act
        var result = list.Pagination(1, 10).ToList();

        // Assert
        result.Should().HaveCount(10);
        result.First().Should().Be(1);
        result.Last().Should().Be(10);
    }

    [Fact]
    public void Pagination_WithLastPage_ShouldReturnRemainingItems()
    {
        // Arrange
        var list = Enumerable.Range(1, 25).ToList();

        // Act
        var result = list.Pagination(3, 10).ToList();

        // Assert
        result.Should().HaveCount(5);
        result.First().Should().Be(21);
        result.Last().Should().Be(25);
    }

    [Fact]
    public void Pagination_WithEmptyList_ShouldReturnEmpty()
    {
        // Arrange
        var list = new List<int>();

        // Act
        var result = list.Pagination(1, 10).ToList();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void SetPagination_WithHasNext_ShouldSetHasNextTrue()
    {
        // Arrange
        var response = new PaginationResponse<int>
        {
            Items = Enumerable.Range(1, 11).ToList()
        };
        var query = new PaginationQuery { PageNumber = 1, PageSize = 10 };

        // Act
        response.SetPagination(query);

        // Assert
        response.HasNext.Should().BeTrue();
        response.Items.Should().HaveCount(10);
    }

    [Fact]
    public void SetPagination_WithoutHasNext_ShouldSetHasNextFalse()
    {
        // Arrange
        var response = new PaginationResponse<int>
        {
            Items = Enumerable.Range(1, 10).ToList()
        };
        var query = new PaginationQuery { PageNumber = 1, PageSize = 10 };

        // Act
        response.SetPagination(query);

        // Assert
        response.HasNext.Should().BeFalse();
        response.Items.Should().HaveCount(10);
    }
}

