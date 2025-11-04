# Contributing to Neo Framework

First off, thank you for considering contributing to Neo Framework! 🎉

## 📋 Table of Contents

- [Code of Conduct](#code-of-conduct)
- [How Can I Contribute?](#how-can-i-contribute)
- [Development Setup](#development-setup)
- [Coding Guidelines](#coding-guidelines)
- [Pull Request Process](#pull-request-process)
- [Commit Message Guidelines](#commit-message-guidelines)

---

## Code of Conduct

This project adheres to a Code of Conduct. By participating, you are expected to uphold this code.

### Our Standards

- ✅ Be respectful and inclusive
- ✅ Welcome newcomers and help them learn
- ✅ Focus on what is best for the community
- ✅ Show empathy towards other community members

---

## How Can I Contribute?

### 🐛 Reporting Bugs

Before creating bug reports, please check existing issues. When creating a bug report, include:

- **Description**: Clear description of the problem
- **Steps to Reproduce**: Step-by-step instructions
- **Expected Behavior**: What you expected to happen
- **Actual Behavior**: What actually happened
- **Environment**: .NET version, OS, etc.
- **Code Sample**: Minimal reproducible example

### 💡 Suggesting Enhancements

Enhancement suggestions are tracked as GitHub issues. When creating an enhancement suggestion, include:

- **Use Case**: Why this feature would be useful
- **Proposed Solution**: How you envision it working
- **Alternatives**: Other solutions you've considered

### 📝 Pull Requests

1. Fork the repo and create your branch from `main`
2. Make your changes following our [Coding Guidelines](#coding-guidelines)
3. Add or update tests as needed
4. Ensure all tests pass
5. Update documentation if needed
6. Submit your pull request

---

## Development Setup

### Prerequisites

- .NET 8 SDK or later
- Git
- Your favorite IDE (Visual Studio, Rider, or VS Code)

### Setup Steps

```bash
# 1. Fork and clone the repository
git clone https://github.com/YOUR_USERNAME/Neo-Framework.git
cd Neo-Framework

# 2. Restore dependencies
dotnet restore

# 3. Build the solution
dotnet build

# 4. Run tests
dotnet test

# 5. Create a branch for your feature
git checkout -b feature/your-feature-name
```

---

## Coding Guidelines

We follow strict coding standards based on Clean Architecture and SOLID principles.

### P0 - Critical (Must Follow)

#### Clean Architecture

```csharp
// ✅ GOOD: Domain is independent
public class Customer : BaseAuditableEntity<Guid>
{
    public CustomerName Name { get; private set; }
}

// ❌ BAD: Domain depends on Infrastructure
public class Customer : INotification // MediatR dependency
{
    public string Name { get; set; }
}
```

#### SOLID Principles

- **Single Responsibility**: One class, one reason to change
- **Open/Closed**: Open for extension, closed for modification
- **Liskov Substitution**: Subtypes must be substitutable
- **Interface Segregation**: Many specific interfaces > one general
- **Dependency Inversion**: Depend on abstractions, not concretions

#### Security

- Always validate user input
- Never expose stack traces to users
- Use parameterized queries (EF Core handles this)
- Hash passwords with BCrypt or Argon2

#### Code Quality

- No magic strings/numbers
- Meaningful names (self-documenting)
- No commented code (use git history)
- Max method complexity: 10
- Max class size: 300 lines

### P1 - Important (Production-Ready)

#### CQRS Pattern

```csharp
// Commands: Change state
public record CreateCustomerCommand(...) : IRequest<Result<Guid>>;

// Queries: Read data
public record GetCustomerQuery(Guid Id) : IRequest<CustomerDto>;
```

#### Validation

```csharp
// Always use FluentValidation for DTOs
public class CreateCustomerCommandValidator 
    : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}
```

#### Logging

```csharp
// Use structured logging
_logger.LogInformation(
    "Customer created: {CustomerId} by {UserId}", 
    customer.Id, 
    userId
);
```

### P2 - Advanced (Nice-to-Have)

- Circuit breaker patterns
- Retry policies
- Custom metrics
- Integration tests

---

## Pull Request Process

### Before Submitting

- [ ] Code builds successfully
- [ ] All tests pass
- [ ] New tests added for new features
- [ ] Documentation updated
- [ ] No linting errors
- [ ] Follows coding guidelines

### PR Title Format

Use conventional commits:

```
feat: Add customer export feature
fix: Resolve null reference in UserService
docs: Update README with new examples
refactor: Simplify validation logic
test: Add tests for email validation
```

### PR Description Template

```markdown
## Description
Brief description of changes

## Type of Change
- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation update

## Testing
- [ ] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] Manual testing completed

## Checklist
- [ ] Code follows project guidelines
- [ ] Self-review completed
- [ ] Documentation updated
- [ ] No new warnings
```

---

## Commit Message Guidelines

We follow [Conventional Commits](https://www.conventionalcommits.org/):

### Format

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Types

- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation only
- `style`: Code style (formatting, missing semicolons, etc.)
- `refactor`: Code change that neither fixes a bug nor adds a feature
- `perf`: Performance improvement
- `test`: Adding or updating tests
- `chore`: Maintenance tasks

### Examples

```bash
feat(domain): Add Customer aggregate with value objects

- Add Customer entity
- Add CustomerName value object
- Add Email value object
- Add CustomerCreatedEvent

Closes #123

fix(infrastructure): Resolve null reference in IdpService

The GetRoleByName method could return null, causing
an exception in RemoveUserRealmRole.

Added null check before calling the method.

Fixes #456
```

---

## Questions?

Feel free to open an issue with the `question` label or reach out to the maintainers.

---

**Thank you for contributing to Neo Framework!** 🚀

