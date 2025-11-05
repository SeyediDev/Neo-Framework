using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Neo.Domain.Entities.Base;
using Neo.Domain.Repository;
using Neo.Infrastructure.Data.Repository.Ef;
using System.Reflection;

namespace Neo.Infrastructure.IntegrationTests.Data.Repository;

public class EfRepositoryIntegrationTests : IDisposable
{
    private class TestEntity : BaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    private class TestDbContext : EfDbContext<TestDbContext>
    {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options)
        {
        }

        protected override Assembly ContextAssembly => typeof(TestDbContext).Assembly;

        public DbSet<TestEntity> TestEntities { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<TestEntity>().HasKey(e => e.Id);
            modelBuilder.Entity<TestEntity>().Property(e => e.Name).IsRequired().HasMaxLength(100);
        }
    }

    private class TestCommandRepository : EfCommandRepository<TestEntity, int, TestDbContext>
    {
        public TestCommandRepository(TestDbContext uow) : base(uow)
        {
        }
    }

    private class TestQueryRepository : EfQueryRepository<TestEntity, int, TestDbContext>
    {
        public TestQueryRepository(TestDbContext uow) : base(uow)
        {
        }
    }

    private readonly TestDbContext _context;
    private readonly ICommandRepository<TestEntity, int> _commandRepository;
    private readonly IQueryRepository<TestEntity, int> _queryRepository;
    private readonly IUnitOfWork _unitOfWork;

    public EfRepositoryIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TestDbContext(options);
        _commandRepository = new TestCommandRepository(_context);
        _queryRepository = new TestQueryRepository(_context);
        _unitOfWork = _context;
    }

    [Fact]
    public async Task AddAsync_And_SaveChanges_ShouldPersistEntity()
    {
        // Arrange
        var entity = new TestEntity { Name = "Test Product", Description = "Test Description" };

        // Act
        await _commandRepository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();

        // Assert
        var saved = await _queryRepository.GetByIdAsync(entity.Id, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Test Product");
        saved.Description.Should().Be("Test Description");
    }

    [Fact]
    public async Task Update_And_SaveChanges_ShouldModifyEntity()
    {
        // Arrange
        var entity = new TestEntity { Name = "Original", Description = "Original Desc" };
        await _commandRepository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();

        // Act
        entity.Name = "Updated";
        entity.Description = "Updated Desc";
        _commandRepository.Update(entity);
        await _unitOfWork.SaveChangesAsync();

        // Assert
        var updated = await _queryRepository.GetByIdAsync(entity.Id, CancellationToken.None);
        updated!.Name.Should().Be("Updated");
        updated.Description.Should().Be("Updated Desc");
    }

    [Fact]
    public async Task Remove_And_SaveChanges_ShouldDeleteEntity()
    {
        // Arrange
        var entity = new TestEntity { Name = "To Delete", Description = "Desc" };
        await _commandRepository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();

        // Act
        _commandRepository.Remove(entity);
        await _unitOfWork.SaveChangesAsync();

        // Assert
        var deleted = await _queryRepository.GetByIdAsync(entity.Id, CancellationToken.None);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnAllEntities()
    {
        // Arrange
        var entities = new[]
        {
            new TestEntity { Name = "Entity 1", Description = "Desc 1" },
            new TestEntity { Name = "Entity 2", Description = "Desc 2" },
            new TestEntity { Name = "Entity 3", Description = "Desc 3" }
        };

        foreach (var entity in entities)
        {
            await _commandRepository.AddAsync(entity);
        }
        await _unitOfWork.SaveChangesAsync();

        // Act
        var result = await _queryRepository.GetAllAsync(CancellationToken.None);

        // Assert
        result.Should().HaveCount(3);
        result.Select(e => e.Name).Should().Contain(new[] { "Entity 1", "Entity 2", "Entity 3" });
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithPredicate_ShouldReturnMatchingEntity()
    {
        // Arrange
        var entities = new[]
        {
            new TestEntity { Name = "Apple", Description = "Fruit" },
            new TestEntity { Name = "Carrot", Description = "Vegetable" },
            new TestEntity { Name = "Banana", Description = "Fruit" }
        };

        foreach (var entity in entities)
        {
            await _commandRepository.AddAsync(entity);
        }
        await _unitOfWork.SaveChangesAsync();

        // Act
        var result = await _queryRepository.FirstOrDefaultAsync(
            e => e.Name == "Apple",
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Apple");
        result.Description.Should().Be("Fruit");
    }

    [Fact]
    public async Task AnyAsync_WithPredicate_ShouldReturnCorrectBoolean()
    {
        // Arrange
        var entity = new TestEntity { Name = "Test", Description = "Desc" };
        await _commandRepository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();

        // Act
        var exists = await _queryRepository.AnyAsync(e => e.Name == "Test", CancellationToken.None);
        var notExists = await _queryRepository.AnyAsync(e => e.Name == "NonExistent", CancellationToken.None);

        // Assert
        exists.Should().BeTrue();
        notExists.Should().BeFalse();
    }

    [Fact(Skip = "InMemory database does not support transactions")]
    public async Task Transaction_Rollback_ShouldNotPersistChanges()
    {
        // Note: InMemory database does not support transactions
        // This test should be run with a real database (SQL Server, SQLite)
        // Arrange
        var entity1 = new TestEntity { Name = "Entity 1", Description = "Desc" };

        // Act
        await _unitOfWork.BeginTransactionAsync();
        await _commandRepository.AddAsync(entity1);
        await _unitOfWork.SaveChangesAsync();
        await _unitOfWork.RollbackTransactionAsync();

        // Assert
        var count = await _queryRepository.GetAllAsync(CancellationToken.None);
        count.Should().BeEmpty();
    }

    [Fact(Skip = "InMemory database does not support transactions")]
    public async Task Transaction_Commit_ShouldPersistChanges()
    {
        // Note: InMemory database does not support transactions
        // This test should be run with a real database (SQL Server, SQLite)
        // Arrange
        var entity = new TestEntity { Name = "Committed", Description = "Desc" };

        // Act
        await _unitOfWork.BeginTransactionAsync();
        await _commandRepository.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();
        await _unitOfWork.CommitTransactionAsync();

        // Assert
        var saved = await _queryRepository.GetByIdAsync(entity.Id, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Committed");
    }

    public void Dispose()
    {
        _context?.Dispose();
    }
}

