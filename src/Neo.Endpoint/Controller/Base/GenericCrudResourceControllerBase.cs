using Microsoft.AspNetCore.Authorization;
using Neo.Application.Exceptions;
using Neo.Application.Features.Crud;
using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Concurrency;

namespace Neo.Endpoint.Controller.Base;

/// <summary>Opt-in resource endpoints. Declare a route on the concrete controller.</summary>
[ApiController]
public abstract class GenericCrudControllerBase<TCreate,TUpdate,TRead,TEntity,TKey,TDefinition>(
    ICrudService<TDefinition,TCreate,TUpdate,TRead,TKey> service, TDefinition definition,
    IAuthorizationService authorization) : ControllerBase
    where TCreate : class where TUpdate : class where TRead : class
    where TEntity : class,IEntity<TKey>,IConcurrencyVersion where TKey : struct
    where TDefinition : CrudDefinition<TCreate,TUpdate,TRead,TEntity,TKey>
{
    [HttpGet]
    public Task<IActionResult> List([FromQuery] CrudQuery query, CancellationToken ct) =>
        Execute(CrudOperation.List, async () => Ok(await service.ListAsync(query, ct)));

    [HttpGet("{id}")]
    [ActionName("GetById")]
    public Task<IActionResult> GetById(TKey id, CancellationToken ct) => Execute(CrudOperation.Read, async () =>
    {
        var item = await service.GetAsync(id, ct);
        return item is null ? NotFound() : Ok(item);
    });

    [HttpPost]
    public Task<IActionResult> Create([FromBody] TCreate input, CancellationToken ct) => Execute(CrudOperation.Create, async () =>
    {
        var item = await service.CreateAsync(input, ct);
        return CreatedAtAction("GetById", new { id = item.Id }, item);
    });

    [HttpPut("{id}")]
    public Task<IActionResult> Update(TKey id, [FromBody] CrudUpdate<TUpdate> input, CancellationToken ct) =>
        Execute(CrudOperation.Update, async () => input is null
            ? BadRequest() : Ok(await service.UpdateAsync(id, input.Data, input.ExpectedVersion, ct)));

    [HttpDelete("{id}")]
    public Task<IActionResult> Delete(TKey id, [FromQuery] Guid expectedVersion, CancellationToken ct) => Execute(CrudOperation.Delete, async () =>
    {
        await service.DeleteAsync(id, expectedVersion, ct);
        return NoContent();
    });

    private async Task<IActionResult> Execute(CrudOperation operation, Func<Task<IActionResult>> action)
    {
        if (!definition.Operations.HasFlag(operation)) return StatusCode(StatusCodes.Status405MethodNotAllowed);
        if (!definition.Policies.TryGetValue(operation, out var policy))
            throw new InvalidOperationException("Declare an authorization policy for every enabled operation.");
        if (policy is not null && !(await authorization.AuthorizeAsync(User, definition, policy)).Succeeded)
            return StatusCode(User.Identity?.IsAuthenticated == true ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized);
        // Neo applications may suppress MVC's automatic invalid-model filter.
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try { return await action(); }
        catch (BadRequestException ex) { return Problem(statusCode: 400, title: "Invalid request", detail: ex.Message); }
        catch (ValidationException ex) { return BadRequest(new ValidationProblemDetails(ex.Errors)); }
        catch (Ardalis.GuardClauses.NotFoundException) { return NotFound(); }
        catch (EntityConcurrencyException ex)
        {
            var problem = new ProblemDetails { Status = 409, Title = "Concurrency conflict", Detail = ex.Message };
            problem.Extensions["code"] = ex.Code;
            return Conflict(problem);
        }
    }
}
