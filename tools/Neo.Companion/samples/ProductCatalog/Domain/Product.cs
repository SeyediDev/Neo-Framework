using Neo.Domain.Entities.Base;

namespace ProductCatalog.Domain;

public sealed class Product : BaseEntity<Guid>
{
    public const int MaxNameLength = 200;
    public string Name { get; private set; } = string.Empty;
    public decimal Price { get; private set; }

    // Neo's repository requires new(); EF also uses this constructor.
    // Application code creates products through Create to enforce the invariants.
    public Product() { }

    public static Product Create(string? name, decimal price)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > MaxNameLength)
            throw new ArgumentException($"Name must contain 1–{MaxNameLength} characters.", nameof(name));
        if (price < 0)
            throw new ArgumentOutOfRangeException(nameof(price), "Price cannot be negative.");
        return new Product { Id = Guid.NewGuid(), Name = normalized, Price = price };
    }
}
