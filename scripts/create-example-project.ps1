# PowerShell script to create an example project using Neo Framework
param(
    [string]$ProjectName = "Neo.Example",
    [string]$OutputPath = "examples",
    [string]$NeoVersion = "1.0.0"
)

Write-Host "=============================================================" -ForegroundColor Cyan
Write-Host "[*] Creating Neo Framework Example Project" -ForegroundColor Yellow
Write-Host "=============================================================" -ForegroundColor Cyan

$ScriptRoot = Split-Path -Parent $PSScriptRoot
$ExamplePath = Join-Path $ScriptRoot $OutputPath
$ProjectPath = Join-Path $ExamplePath $ProjectName

# Create example directory
if (-not (Test-Path $ExamplePath)) {
    New-Item -ItemType Directory -Path $ExamplePath -Force | Out-Null
}

Write-Host "`n[1/5] Creating Web API project..." -ForegroundColor Yellow
Push-Location $ExamplePath
dotnet new webapi -n $ProjectName -f net8.0 --use-controllers --no-https
Pop-Location

Write-Host "`n[2/5] Adding Neo Framework packages..." -ForegroundColor Yellow
$LocalNuGetPath = "D:\Projects\NuGetPackages"
if (-not (Test-Path $LocalNuGetPath)) {
    $LocalNuGetPath = Join-Path $ScriptRoot "artifacts"
}

Push-Location $ProjectPath

# Add local NuGet source if packages exist locally
if (Test-Path $LocalNuGetPath) {
    dotnet nuget add source $LocalNuGetPath --name "LocalNeoPackages" | Out-Null
}

# Add Neo packages
dotnet add package Neo.Common --version $NeoVersion --source $LocalNuGetPath
dotnet add package Neo.Domain --version $NeoVersion --source $LocalNuGetPath
dotnet add package Neo.Application --version $NeoVersion --source $LocalNuGetPath
dotnet add package Neo.Infrastructure --version $NeoVersion --source $LocalNuGetPath
dotnet add package Neo.Endpoint --version $NeoVersion --source $LocalNuGetPath

Write-Host "`n[3/5] Adding required packages..." -ForegroundColor Yellow
dotnet add package Microsoft.EntityFrameworkCore.SqlServer
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Swashbuckle.AspNetCore

Write-Host "`n[4/5] Creating project structure..." -ForegroundColor Yellow

# Create folders
$Folders = @(
    "Domain\Entities",
    "Domain\ValueObjects",
    "Application\Features\Products\Commands",
    "Application\Features\Products\Queries",
    "Application\Features\Products\Dto",
    "Infrastructure\Data",
    "Controllers"
)

foreach ($folder in $Folders) {
    $FullPath = Join-Path $ProjectPath $folder
    if (-not (Test-Path $FullPath)) {
        New-Item -ItemType Directory -Path $FullPath -Force | Out-Null
    }
}

Write-Host "`n[5/5] Creating example files..." -ForegroundColor Yellow

# Create example entity
$ProductEntity = @"
namespace $ProjectName.Domain.Entities;

using Neo.Domain.Entities.Base;

public class Product : BaseEntity<int>
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
}
"@
Set-Content -Path (Join-Path $ProjectPath "Domain\Entities\Product.cs") -Value $ProductEntity

# Create example command
$CreateProductCommand = @"
namespace $ProjectName.Application.Features.Products.Commands;

using MediatR;

public record CreateProductCommand(string Name, string Description, decimal Price, int Stock) : IRequest<int>;
"@
Set-Content -Path (Join-Path $ProjectPath "Application\Features\Products\Commands\CreateProductCommand.cs") -Value $CreateProductCommand

# Create example query
$GetProductsQuery = @"
namespace $ProjectName.Application.Features.Products.Queries;

using MediatR;
using $ProjectName.Application.Features.Products.Dto;

public record GetProductsQuery() : IRequest<List<ProductDto>>;
"@
Set-Content -Path (Join-Path $ProjectPath "Application\Features\Products\Queries\GetProductsQuery.cs") -Value $GetProductsQuery

# Create example DTO
$ProductDto = @"
namespace $ProjectName.Application.Features.Products.Dto;

public record ProductDto(int Id, string Name, string Description, decimal Price, int Stock);
"@
Set-Content -Path (Join-Path $ProjectPath "Application\Features\Products\Dto\ProductDto.cs") -Value $ProductDto

# Create example controller
$ProductsController = @"
namespace $ProjectName.Controllers;

using MediatR;
using Microsoft.AspNetCore.Mvc;
using Neo.Endpoint.Controller;
using $ProjectName.Application.Features.Products.Commands;
using $ProjectName.Application.Features.Products.Queries;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : AppControllerBase
{
    public ProductsController(ISender sender) : base(sender)
    {
    }

    [HttpGet]
    public async Task<IActionResult> GetProducts()
    {
        var query = new GetProductsQuery();
        var result = await Sender.Send(query);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductCommand command)
    {
        var result = await Sender.Send(command);
        return CreatedAtAction(nameof(GetProducts), new { id = result }, result);
    }
}
"@
Set-Content -Path (Join-Path $ProjectPath "Controllers\ProductsController.cs") -Value $ProductsController

# Create README
$ExampleReadme = @"
# $ProjectName

This is an example project demonstrating how to use the Neo Framework.

## Features

- Clean Architecture
- CQRS Pattern with MediatR
- Entity Framework Core
- RESTful API

## Getting Started

1. Update the connection string in `appsettings.json`
2. Run migrations: `dotnet ef database update`
3. Run the project: `dotnet run`

## Project Structure

- `Domain/` - Domain entities and value objects
- `Application/` - Application logic (Commands, Queries, DTOs)
- `Infrastructure/` - Infrastructure implementations
- `Controllers/` - API Controllers

## Example Endpoints

- `GET /api/products` - Get all products
- `POST /api/products` - Create a new product
"@
Set-Content -Path (Join-Path $ProjectPath "README.md") -Value $ExampleReadme

Pop-Location

Write-Host "`n=============================================================" -ForegroundColor Green
Write-Host "[SUCCESS] Example project created!" -ForegroundColor Green
Write-Host "=============================================================" -ForegroundColor Green
Write-Host "`nProject Location: $ProjectPath" -ForegroundColor Cyan
Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "  1. cd $ProjectPath" -ForegroundColor Gray
Write-Host "  2. dotnet restore" -ForegroundColor Gray
Write-Host "  3. dotnet run" -ForegroundColor Gray

