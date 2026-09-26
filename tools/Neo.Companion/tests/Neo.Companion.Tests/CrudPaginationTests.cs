using System.Linq.Expressions;
using Neo.Application.Exceptions;
using Moq;
using Neo.Application.Features.GenericEntity.GenericService;
using Neo.Application.Features.GenericEntity.Queries;
using Neo.Application.Models;
using Neo.Domain.Dto;
using Neo.Domain.Entities.Base;
using Neo.Domain.Repository;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class CrudPaginationTests
{
    [Fact]
    public void Pages_have_no_gaps_and_has_next_resets()
    {
        var all = Enumerable.Range(1, 25).ToList();
        var pages = new List<int>();
        for (var number = 1; number <= 3; number++)
        {
            var page = new PaginationResponse<int> { Items = all, HasNext = true };
            page.SetPagination(new() { PageNumber = number, PageSize = 10 });
            pages.AddRange(page.Items!); Assert.Equal(number < 3, page.HasNext);
        }
        Assert.Equal(all, pages);
    }
    [Theory]
    [InlineData(0, 10)] [InlineData(1, 0)] [InlineData(1, 201)] [InlineData(int.MaxValue, 200)]
    public async Task Invalid_pages_are_rejected_before_database_access(int number, int size)
    {
        var repo = new Mock<IQueryRepository<Entity,int>>(MockBehavior.Strict);
        await Assert.ThrowsAsync<ValidationException>(() => Handler(repo.Object).Send(new() { PageNumber = number, PageSize = size }, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Handler_requests_a_database_page_with_stable_order_and_no_tracking()
    {
        var repo = new Mock<IQueryRepository<Entity,int>>(MockBehavior.Strict);
        repo.Setup(x => x.GetPagedAsync(2, 10, It.IsAny<CancellationToken>(), null,
            It.IsAny<Func<IQueryable<Entity>,IOrderedQueryable<Entity>>>(), true))
            .Callback<int,int,CancellationToken,Expression<Func<Entity,bool>>?,Func<IQueryable<Entity>,IOrderedQueryable<Entity>>?,bool>((_,_,_,_,order,_) =>
                Assert.Equal(new[] { 1, 2 }, order!(new[] { new Entity { Id=2 }, new Entity { Id=1 } }.AsQueryable()).Select(x => x.Id)))
            .ReturnsAsync((Enumerable.Range(11,10).Select(x => new Entity { Id=x }), true));
        var result = await Handler(repo.Object).Send(new() { PageNumber=2, PageSize=10 }, TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(11,10).Select(x => (int?)x), result!.Items!.Select(x=>x.Id)); Assert.True(result.HasNext);
        repo.VerifyAll();
    }
    private static GetAllGenericEntityCommandHandler<Dto,Entity,int> Handler(IQueryRepository<Entity,int> repo)
    {
        var service = new Mock<IGenericServiceHandler>();
        service.Setup(x => x.Handle(It.IsAny<GetAllGenericEntityCommand<Dto,Entity,int>>(),
            It.IsAny<Func<GetAllGenericEntityCommand<Dto,Entity,int>,CancellationToken,Task<GetAllGenericEntityResponse<Dto,int>?>>>(), It.IsAny<CancellationToken>()))
            .Returns<GetAllGenericEntityCommand<Dto,Entity,int>,Func<GetAllGenericEntityCommand<Dto,Entity,int>,CancellationToken,Task<GetAllGenericEntityResponse<Dto,int>?>>,CancellationToken>((r,next,ct)=>next(r,ct));
        return new(repo,service.Object);
    }
    public sealed class Entity : BaseEntity<int>;
    public sealed class Dto : IDto<int> { public int? Id { get; set; } }
}
