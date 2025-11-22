# Neo Framework Examples

این فایل شامل راهنمای استفاده از Neo Framework و مثال‌های عملی است.

## 📦 ایجاد پروژه نمونه

برای ایجاد یک پروژه نمونه کامل با Neo Framework:

```powershell
pwsh scripts/create-example-project.ps1 -ProjectName "MyNeoApp" -NeoVersion "1.0.0"
```

## 🧪 اجرای تست‌ها با Coverage

برای اجرای تست‌ها و دریافت گزارش Code Coverage:

```powershell
pwsh scripts/test-with-coverage.ps1
```

گزارش HTML در پوشه `coverage/report` ایجاد می‌شود.

## 📊 انواع تست‌ها

### Unit Tests
تست‌های واحد برای هر پروژه:
- `Neo.Common.Tests` - 29 تست
- `Neo.Domain.Tests` - 22 تست
- `Neo.Application.Tests` - 15 تست
- `Neo.Infrastructure.Tests` - 14 تست
- `Neo.Endpoint.Tests` - 14 تست
- `Neo.Infrastructure.Hangfire.Tests` - 4 تست

### Integration Tests
تست‌های یکپارچه برای:
- `Neo.Infrastructure.IntegrationTests` - تست Repository با EF Core
- `Neo.Endpoint.IntegrationTests` - تست Controllers

## 🚀 استفاده از Neo Framework

### 1. نصب پکیج‌ها

```xml
<ItemGroup>
  <PackageReference Include="Neo.Common" Version="1.0.0" />
  <PackageReference Include="Neo.Domain" Version="1.0.0" />
  <PackageReference Include="Neo.Application" Version="1.0.0" />
  <PackageReference Include="Neo.Infrastructure" Version="1.0.0" />
  <PackageReference Include="Neo.Endpoint" Version="1.0.0" />
</ItemGroup>
```

### 2. تنظیم Dependency Injection

```csharp
// Program.cs
builder.Services.AddNeoCommon();
builder.Services.AddNeoDomain();
builder.Services.AddNeoApplication();
builder.Services.AddNeoInfrastructure(options =>
{
    options.UseSqlServer(connectionString);
});
builder.Services.AddNeoEndpoint();
```

### 3. ایجاد Entity

```csharp
public class Product : BaseEntity<int>
{
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}
```

### 4. ایجاد Command و Handler

```csharp
// Command
public record CreateProductCommand(string Name, decimal Price) : IRequest<int>;

// Handler
public class CreateProductHandler : IRequestHandler<CreateProductCommand, int>
{
    private readonly ICommandRepository<Product, int> _repository;
    private readonly IUnitOfWork _unitOfWork;

    public CreateProductHandler(
        ICommandRepository<Product, int> repository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<int> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        var product = new Product
        {
            Name = request.Name,
            Price = request.Price
        };

        await _repository.AddAsync(product);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return product.Id;
    }
}
```

### 5. ایجاد Controller

```csharp
[ApiController]
[Route("api/[controller]")]
public class ProductsController : AppControllerBase
{
    public ProductsController(ISender sender) : base(sender)
    {
    }

    [HttpPost]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductCommand command)
    {
        var result = await Sender.Send(command);
        return CreatedAtAction(nameof(GetProduct), new { id = result }, result);
    }
}
```

## 📚 مستندات بیشتر

- [README.md](README.md) - راهنمای کلی
- [NEO-FRAMEWORK-GUIDE.md](NEO-FRAMEWORK-GUIDE.md) - راهنمای کامل
- [CONTRIBUTING.md](CONTRIBUTING.md) - راهنمای مشارکت










