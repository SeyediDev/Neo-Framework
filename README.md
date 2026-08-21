# Neo Framework

<div align="center">

![Neo Framework](https://img.shields.io/badge/Neo-Framework-blue)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Build Status](https://img.shields.io/badge/build-passing-brightgreen)]()

**A modern, lightweight .NET framework for building clean architecture applications**

[Features](#-features) • [Installation](#-installation) • [Quick Start](#-quick-start) • [Documentation](#-documentation) • [Contributing](#-contributing)

</div>

---

## 🌟 What is Neo?

Neo is a production-ready .NET 10 framework that implements **Clean Architecture**, **CQRS**, and **Domain-Driven Design** patterns out of the box. Built with best practices and enterprise-grade features, Neo helps you build scalable, maintainable applications faster.

### Why Neo?

- ✅ **Clean Architecture** - Clear separation of concerns with well-defined layers
- ✅ **CQRS Pattern** - Built on MediatR with pipeline behaviors
- ✅ **Domain-Driven Design** - Rich domain models, value objects, and domain events
- ✅ **Production-Ready** - Outbox pattern, background jobs, caching, and more
- ✅ **Type-Safe** - Nullable reference types enabled, strict validation
- ✅ **Persian Support** - Full Farsi/Persian utilities included
- ✅ **Extensible** - Easy to customize and extend

---

## 🚀 Features

### Core Features

| Feature | Description |
|---------|-------------|
| **Clean Architecture** | Domain → Application → Infrastructure → Endpoint layers |
| **CQRS** | Command Query Responsibility Segregation with MediatR |
| **Domain Events** | Publish-subscribe pattern for domain events |
| **Value Objects** | Type-safe domain primitives |
| **Validation** | FluentValidation integration |
| **Mapping** | AutoMapper + Mapster support |

### Infrastructure Features

| Feature | Package | Description |
|---------|---------|-------------|
| **Outbox Pattern** | `Neo.Application` | Reliable message delivery with transactional guarantees |
| **Background Jobs** | `Neo.Infrastructure.Hangfire` | Recurring jobs and background processing |
| **Caching** | `Neo.Infrastructure` | Memory + Redis distributed caching |
| **Object Storage** | `Neo.Infrastructure` | MinIO integration for file storage |
| **Identity** | `Neo.Infrastructure` | Keycloak integration for authentication |
| **Captcha** | `Neo.Application` | Google reCAPTCHA support |

### Developer Experience

- 🔧 **Generic CRUD** - Ready-to-use generic controllers and handlers
- 📝 **Telemetry** - Built-in OpenTelemetry support
- 🔐 **Security** - JWT, authorization attributes, input validation
- 🌍 **Multilingual** - Culture-based term management
- 📊 **Health Checks** - EF Core health checks included

---

## 📦 Installation

### NuGet Packages

```bash
# Core packages (required)
dotnet add package Neo.Domain
dotnet add package Neo.Application
dotnet add package Neo.Infrastructure

# Endpoint layer (for Web APIs)
dotnet add package Neo.Endpoint

# Optional: Background jobs
dotnet add package Neo.Infrastructure.Hangfire
```

### Package Overview

| Package | Purpose | Dependencies |
|---------|---------|-------------|
| **Neo.Common** | Shared utilities and extensions | - |
| **Neo.Domain** | Domain entities, value objects, interfaces | Common |
| **Neo.Application** | CQRS, validation, application logic | Domain |
| **Neo.Infrastructure** | EF Core, caching, external services | Application |
| **Neo.Infrastructure.Hangfire** | Background job processing | Infrastructure |
| **Neo.Endpoint** | API controllers, exception handling | Application |

---

## 🏃 Quick Start

### 1. Create a New Project

```bash
dotnet new webapi -n MyApp
cd MyApp
dotnet add package Neo.Domain
dotnet add package Neo.Application
dotnet add package Neo.Infrastructure
dotnet add package Neo.Endpoint
```

### 2. Configure Services

```csharp
// Program.cs
using Neo.Application;
using Neo.Domain;
using Neo.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Add Neo services
builder.Services
    .AddNeoDomain()
    .AddNeoApplication()
    .AddNeoInfrastructure(builder.Configuration)
    .AddNeoEndpoint();

var app = builder.Build();

app.UseNeoExceptionHandler();
app.MapControllers();

app.Run();
```

### 3. Create Your First Feature

```csharp
// Domain Layer - Customer.cs
public class Customer : BaseAuditableEntity<Guid>
{
    public CustomerName Name { get; private set; }
    public Email Email { get; private set; }

    public static Customer Create(CustomerName name, Email email)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Email = email
        };
        
        customer.AddDomainEvent(new CustomerCreatedEvent(customer.Id));
        return customer;
    }
}

// Application Layer - CreateCustomerCommand.cs
public record CreateCustomerCommand(string Name, string Email) 
    : IRequest<Result<Guid>>;

public class CreateCustomerCommandValidator 
    : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

public class CreateCustomerCommandHandler 
    : IRequestHandler<CreateCustomerCommand, Result<Guid>>
{
    private readonly IRepository<Customer> _repository;

    public async Task<Result<Guid>> Handle(
        CreateCustomerCommand request, 
        CancellationToken ct)
    {
        var name = CustomerName.Create(request.Name);
        var email = Email.Create(request.Email);
        
        var customer = Customer.Create(name, email);
        
        await _repository.AddAsync(customer, ct);
        await _repository.SaveChangesAsync(ct);
        
        return Result.Success(customer.Id);
    }
}

// Endpoint Layer - CustomersController.cs
[ApiController]
[Route("api/v1/customers")]
public class CustomersController : AppControllerBase
{
    [HttpPost]
    public async Task<ActionResult<Guid>> Create(
        CreateCustomerCommand command)
    {
        var result = await Mediator.Send(command);
        return result.IsSuccess 
            ? Ok(result.Value) 
            : BadRequest(result.Error);
    }
}
```

---

## 📚 Documentation

### Architecture Layers

```
┌─────────────────────────────────────────┐
│           Endpoint Layer                │  ← API Controllers, Middleware
│         (Neo.Endpoint)                  │
├─────────────────────────────────────────┤
│        Infrastructure Layer             │  ← EF Core, External Services
│      (Neo.Infrastructure)               │
├─────────────────────────────────────────┤
│        Application Layer                │  ← CQRS, Validation, Logic
│       (Neo.Application)                 │
├─────────────────────────────────────────┤
│           Domain Layer                  │  ← Entities, Value Objects
│          (Neo.Domain)                   │
├─────────────────────────────────────────┤
│           Common Layer                  │  ← Shared Utilities
│          (Neo.Common)                   │
└─────────────────────────────────────────┘
```

### Key Concepts

- **[Clean Architecture](docs/architecture.md)** - Layer responsibilities and dependencies
- **[CQRS Pattern](docs/cqrs.md)** - Commands vs Queries
- **[Domain-Driven Design](docs/ddd.md)** - Aggregates, entities, value objects
- **[Outbox Pattern](docs/outbox.md)** - Reliable messaging
- **[Background Jobs](docs/background-jobs.md)** - Hangfire integration

---

## 🤝 Contributing

We welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

### Development Setup

```bash
# Clone the repository
git clone https://github.com/Neo-Framework/Neo-Framework.git
cd Neo-Framework

# Restore packages
dotnet restore

# Build solution
dotnet build

# Run tests
dotnet test
```

---

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## 👤 Author

**Javad Seyedi**

- GitHub: [@JavadSayedi](https://github.com/JavadSayedi)
- LinkedIn: [Javad Seyedi](https://linkedin.com/in/javadseyedi)

---

## ⭐ Show your support

Give a ⭐️ if this project helped you!

---

## 🙏 Acknowledgments

- Inspired by [Clean Architecture](https://blog.cleancoder.com/uncle-bob/2012/010/13/the-clean-architecture.html) by Robert C. Martin
- Built with [MediatR](https://github.com/jbogard/MediatR)
- Powered by [.NET 10](https://dotnet.microsoft.com/)

---

<div align="center">

**Made with ❤️ by Javad Seyedi**

</div>



