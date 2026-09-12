using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Ardalis.GuardClauses;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Neo.Application.Features.GenericEntity.Commands;
using Neo.Application.Features.GenericEntity.GenericService;
using Neo.Application.Features.GenericEntity.Queries;
using Neo.Domain.Dto;
using Neo.Domain.Entities.Base;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Client;
using Neo.Domain.Repository;
using Neo.Endpoint.Controller.Base;

namespace Neo.Endpoint.IntegrationTests.Controller;

public class GenericCrudControllerTests
{
    [Theory]
    [InlineData("/api/v1/admin/CrudProbe")]
    [InlineData("/api/v1/admin/CrudProbe/")]
    public async Task Created_location_can_be_followed_and_body_has_generated_id(string path)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(CrudProbeController).Assembly)
            .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new ProbeControllersOnly()));
        builder.Services.AddApiVersioning().AddMvc();
        var dto = new CrudProbeDto { Name = "original" };
        builder.Services.AddSingleton(Proxy<ICreateGenericEntityCommandHandler<CrudProbeDto, CrudProbeEntity, int>>((_, args) =>
        {
            dto = ((CreateGenericEntityCommand<CrudProbeDto, CrudProbeEntity, int>)args[0]!).Dto;
            return Task.FromResult<int?>(42);
        }));
        builder.Services.AddSingleton(Proxy<IGetGenericEntityCommandHandler<CrudProbeDto, CrudProbeEntity, int>>((_, args) =>
            Task.FromResult(((GetGenericEntityCommand<CrudProbeDto, CrudProbeEntity, int>)args[0]!).Id == 42 ? dto : null)));
        builder.Services.AddSingleton(Unexpected<ICultureTermQueryRepository>());
        builder.Services.AddSingleton(Unexpected<ICommandRepository<CultureTerm, int>>());
        builder.Services.AddSingleton(Unexpected<IRequesterUser>());
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(path, new CrudProbeDto { Name = "keyboard" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(42, (await response.Content.ReadFromJsonAsync<CrudProbeDto>(TestContext.Current.CancellationToken))!.Id);
        Assert.NotNull(response.Headers.Location);
        using var retrieved = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        Assert.Equal("keyboard", (await retrieved.Content.ReadFromJsonAsync<CrudProbeDto>(TestContext.Current.CancellationToken))!.Name);
    }

    [Fact]
    public async Task Create_without_key_returns_bad_request_without_localization()
    {
        var controller = new LocalizedProbeController();
        var result = await controller.CreateAsync(
            Proxy<ICreateGenericEntityCommandHandler<CrudProbeDto, CrudProbeEntity, int>>((_, _) => Task.FromResult<int?>(null)),
            Unexpected<IRequesterUser>(), Unexpected<ICommandRepository<CultureTerm, int>>(),
            Unexpected<ICultureTermQueryRepository>(), new(), TestContext.Current.CancellationToken);
        Assert.IsType<BadRequest>(result.Result);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 1)]
    [InlineData(1, null)]
    [InlineData(1, 2)]
    public async Task Update_rejects_missing_or_mismatched_ids_before_calling_services(int? routeId, int? bodyId)
    {
        var result = await new CrudProbeController().UpdateAsync(
            Unexpected<IUpdateGenericEntityCommandHandler<CrudProbeDto, CrudProbeEntity, int>>(),
            Unexpected<IRequesterUser>(), Unexpected<ICommandRepository<CultureTerm, int>>(),
            Unexpected<ICultureTermQueryRepository>(), routeId, new() { Id = bodyId }, TestContext.Current.CancellationToken);
        Assert.IsType<BadRequest>(result.Result);
    }

    [Fact]
    public async Task Missing_update_propagates_not_found_without_writing_translations()
    {
        var repository = Proxy<ICommandRepository<CrudProbeEntity, int>>((name, _) =>
        {
            Assert.Equal("GetAsync", name);
            return Task.FromResult<CrudProbeEntity?>(null);
        });
        var service = Proxy<IGenericServiceHandler>((_, args) => ((Delegate)args[1]!).DynamicInvoke(args[0], args[2]));
        var handler = new UpdateGenericEntityCommandHandler<CrudProbeDto, CrudProbeEntity, int>(repository, service);
        await Assert.ThrowsAsync<NotFoundException>(async () => await new LocalizedProbeController().UpdateAsync(handler,
            Unexpected<IRequesterUser>(), Unexpected<ICommandRepository<CultureTerm, int>>(),
            Unexpected<ICultureTermQueryRepository>(), 42, new() { Id = 42 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Nonlocalized_guid_delete_does_not_access_translation_services()
    {
        var result = await new GuidProbeController().DeleteAsync(
            Proxy<IDeleteGenericEntityCommandHandler<GuidProbeEntity, Guid>>((_, _) => Task.FromResult(Unit.Value)),
            Unexpected<ICultureTermQueryRepository>(), Unexpected<ICommandRepository<CultureTerm, int>>(),
            Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.IsType<NoContent>(result.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Localized_delete_saves_only_matching_terms_and_handles_empty_results(bool found)
    {
        var term = new CultureTerm { SubjectId = 42 };
        var unrelated = new CultureTerm { SubjectId = 99 };
        int saves = 0, updates = 0;
        var token = TestContext.Current.CancellationToken;
        var uow = Proxy<IUnitOfWork>((name, args) =>
        {
            Assert.Equal("SaveChangesAsync", name);
            Assert.Equal(token, args[0]);
            saves++;
            return Task.FromResult(1);
        });
        var repository = Proxy<ICommandRepository<CultureTerm, int>>((name, args) =>
        {
            if (name == "get_UnitOfWork") return uow;
            Assert.Equal("Update", name);
            Assert.Same(term, args[0]);
            updates++;
            return null;
        });
        var terms = new Dictionary<int, List<CultureTerm>> { [99] = [unrelated] };
        if (found) terms[42] = [term];
        var result = await new LocalizedProbeController().DeleteAsync(
            Proxy<IDeleteGenericEntityCommandHandler<CrudProbeEntity, int>>((_, _) => Task.FromResult(Unit.Value)),
            Proxy<ICultureTermQueryRepository>((_, _) => Task.FromResult(terms)), repository, 42, token);
        Assert.IsType<NoContent>(result.Result);
        Assert.Equal(found ? 1 : 0, saves);
        Assert.Equal(found ? 1 : 0, updates);
        Assert.False(unrelated.IsDeleted);
        Assert.Equal(found, term.IsDeleted);
    }

    [Fact]
    public async Task Missing_translation_preserves_original_field()
    {
        var dto = new CrudProbeDto { Id = 42, Name = "original" };
        var result = await new LocalizedProbeController().GetByIdAsync(
            Proxy<IGetGenericEntityCommandHandler<CrudProbeDto, CrudProbeEntity, int>>((_, _) => Task.FromResult<CrudProbeDto?>(dto)),
            Proxy<ICultureTermQueryRepository>((_, _) => Task.FromResult(new Dictionary<int, List<CultureTerm>> { [42] = [] })),
            42, TestContext.Current.CancellationToken);
        Assert.Equal("original", Assert.IsType<Ok<CrudProbeDto>>(result.Result).Value!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Localized_writes_save_changes_and_forward_cancellation(bool create)
    {
        int saves = 0, added = 0;
        var token = TestContext.Current.CancellationToken;
        var uow = Proxy<IUnitOfWork>((name, args) =>
        {
            Assert.Equal("SaveChangesAsync", name);
            Assert.Equal(token, args[0]);
            saves++;
            return Task.FromResult(1);
        });
        var repository = Proxy<ICommandRepository<CultureTerm, int>>((name, args) =>
        {
            if (name == "get_UnitOfWork") return uow;
            Assert.Equal("AddAsync", name);
            Assert.Equal(token, args[1]);
            var term = Assert.IsType<CultureTerm>(args[0]);
            Assert.Equal(42, term.SubjectId);
            Assert.Equal("translated", term.Term);
            added++;
            return Task.CompletedTask;
        });
        var requester = Proxy<IRequesterUser>((_, args) =>
        {
            Assert.Equal(token, args[0]);
            return Task.FromResult(new LanguageId(1));
        });
        var query = Proxy<ICultureTermQueryRepository>((_, _) => Task.FromResult(new Dictionary<int, List<CultureTerm>>()));
        var controller = new LocalizedProbeController();
        var dto = new CrudProbeDto { Id = create ? null : 42, Name = "translated" };
        if (create)
            await controller.CreateAsync(Proxy<ICreateGenericEntityCommandHandler<CrudProbeDto, CrudProbeEntity, int>>((_, _) => Task.FromResult<int?>(42)),
                requester, repository, query, dto, token);
        else
            await controller.UpdateAsync(Proxy<IUpdateGenericEntityCommandHandler<CrudProbeDto, CrudProbeEntity, int>>((_, _) => Task.FromResult(Unit.Value)),
                requester, repository, query, 42, dto, token);
        Assert.Equal(1, added);
        Assert.Equal(1, saves);
        Assert.Equal(42, dto.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Real_delete_handler_reports_missing_entity_and_does_not_save(bool? removed)
    {
        var token = TestContext.Current.CancellationToken;
        var repository = Proxy<ICommandRepository<CrudProbeEntity, int>>((name, args) =>
        {
            Assert.Equal("RemoveAsync", name);
            Assert.Equal(token, args[1]);
            return Task.FromResult(removed);
        });
        var service = Proxy<IGenericServiceHandler>((_, args) => ((Delegate)args[1]!).DynamicInvoke(args[0], args[2]));
        var handler = new DeleteGenericEntityCommandHandler<CrudProbeEntity, int>(repository, service);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Send(new() { Id = 42 }, token));
    }

    [Fact]
    public async Task Real_delete_handler_saves_successful_removal()
    {
        var token = TestContext.Current.CancellationToken;
        int saves = 0;
        var uow = Proxy<IUnitOfWork>((name, args) =>
        {
            Assert.Equal("SaveChangesAsync", name);
            Assert.Equal(token, args[0]);
            saves++;
            return Task.FromResult(1);
        });
        var repository = Proxy<ICommandRepository<CrudProbeEntity, int>>((name, args) =>
        {
            if (name == "get_UnitOfWork") return uow;
            Assert.Equal("RemoveAsync", name);
            Assert.Equal(token, args[1]);
            return Task.FromResult<bool?>(true);
        });
        var service = Proxy<IGenericServiceHandler>((_, args) => ((Delegate)args[1]!).DynamicInvoke(args[0], args[2]));
        await new DeleteGenericEntityCommandHandler<CrudProbeEntity, int>(repository, service).Send(new() { Id = 42 }, token);
        Assert.Equal(1, saves);
    }

    private static T Unexpected<T>() where T : class => Proxy<T>((name, _) => throw new InvalidOperationException($"Unexpected call: {name}"));
    private static T Proxy<T>(Func<string, object?[], object?> call) where T : class
    {
        T proxy = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)proxy).Call = call;
        return proxy;
    }

    public class ServiceProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!.Name, args!);
    }

    private sealed class ProbeControllersOnly : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(CrudProbeController).GetTypeInfo());
        }
    }
}

public class CrudProbeDto : IDto<int>
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
}
public class CrudProbeEntity : BaseEntity<int>;
public class CrudProbeController : GenericCrudControllerBase<CrudProbeDto, CrudProbeEntity, int>;
[NonController]
public class LocalizedProbeController : CrudProbeController
{
    protected override List<string> CultureFields { get; } = [nameof(CrudProbeDto.Name)];
    protected override string GetResourceLocation(int id) => $"/resources/{id}";
}
public class GuidProbeDto : IDto<Guid> { public Guid? Id { get; set; } }
public class GuidProbeEntity : BaseEntity<Guid>;
[NonController]
public class GuidProbeController : GenericCrudControllerBase<GuidProbeDto, GuidProbeEntity, Guid>;
