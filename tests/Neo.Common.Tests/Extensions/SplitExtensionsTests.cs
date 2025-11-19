using FluentAssertions;
using Neo.Common.Extensions;

namespace Neo.Common.Tests.Extensions;

public class SplitExtensionsTests
{
    [Fact]
    public void SplitList_WithEmptyList_ShouldReturnEmpty()
    {
        // Arrange
        var list = new List<int>();

        // Act
        var result = SplitExtensions.SplitList(list, 50).ToList();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void SplitList_WithSmallerListThanSize_ShouldReturnSingleChunk()
    {
        // Arrange
        var list = new List<int> { 1, 2, 3 };

        // Act
        var result = SplitExtensions.SplitList(list, 50).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void SplitList_WithExactSize_ShouldReturnSingleChunk()
    {
        // Arrange
        var list = Enumerable.Range(1, 50).ToList();

        // Act
        var result = SplitExtensions.SplitList(list, 50).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].Should().HaveCount(50);
    }

    [Fact]
    public void SplitList_WithLargerList_ShouldSplitIntoMultipleChunks()
    {
        // Arrange
        var list = Enumerable.Range(1, 125).ToList();

        // Act
        var result = SplitExtensions.SplitList(list, 50).ToList();

        // Assert
        result.Should().HaveCount(3);
        result[0].Should().HaveCount(50);
        result[1].Should().HaveCount(50);
        result[2].Should().HaveCount(25);
    }

    [Fact]
    public void SplitList_WithCustomSize_ShouldRespectSize()
    {
        // Arrange
        var list = Enumerable.Range(1, 25).ToList();

        // Act
        var result = SplitExtensions.SplitList(list, 10).ToList();

        // Assert
        result.Should().HaveCount(3);
        result[0].Should().HaveCount(10);
        result[1].Should().HaveCount(10);
        result[2].Should().HaveCount(5);
    }

    [Fact]
    public void RunBatch_WithValidBatchSize_ShouldExecuteActionForEachBatch()
    {
        // Arrange
        var list = Enumerable.Range(1, 25).ToList();
        var executedBatches = new List<List<int>>();

        // Act
        list.RunBatch(10, batch => executedBatches.Add(batch));

        // Assert
        executedBatches.Should().HaveCount(3);
        executedBatches[0].Should().HaveCount(10);
        executedBatches[1].Should().HaveCount(10);
        executedBatches[2].Should().HaveCount(5);
    }

    [Fact]
    public void RunBatch_WithZeroBatchSize_ShouldExecuteActionOnce()
    {
        // Arrange
        var list = new List<int> { 1, 2, 3 };
        var executedCount = 0;

        // Act
        list.RunBatch(0, batch => executedCount++);

        // Assert
        executedCount.Should().Be(1);
    }

    [Fact]
    public void RunBatch_WithNegativeBatchSize_ShouldExecuteActionOnce()
    {
        // Arrange
        var list = new List<int> { 1, 2, 3 };
        var executedCount = 0;

        // Act
        list.RunBatch(-1, batch => executedCount++);

        // Assert
        executedCount.Should().Be(1);
    }

    [Fact]
    public void RunBatch_WithEmptyList_ShouldNotExecuteAction()
    {
        // Arrange
        var list = new List<int>();
        var executedCount = 0;

        // Act
        list.RunBatch(10, batch => executedCount++);

        // Assert
        executedCount.Should().Be(0);
    }
}

