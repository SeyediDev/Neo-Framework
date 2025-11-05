using System.Security.Cryptography;
using System.Text;

namespace Neo.Application.Features.Outbox;

/// <summary>
/// Utility class for hashing idempotency keys to improve security and performance.
/// Uses SHA-256 for consistent hashing with salt for additional security.
/// </summary>
public static class IdempotencyKeyHasher
{
    private const string SaltPrefix = "NeoIdempotency";
    
    /// <summary>
    /// Creates a composite key from tenantId and idempotencyKey, then hashes it.
    /// </summary>
    /// <param name="tenantId">The tenant identifier</param>
    /// <param name="idempotencyKey">The original idempotency key</param>
    /// <returns>Hashed composite key</returns>
    public static string CreateHashedKey(string tenantId, string idempotencyKey)
    {
        // Create composite key: tenantId:idempotencyKey
        var compositeKey = $"{tenantId}:{idempotencyKey}";
        
        // Add salt for additional security
        var saltedKey = $"{SaltPrefix}:{compositeKey}";
        
        // Hash using SHA-256
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(saltedKey));
        
        // Convert to hex string
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
    
    /// <summary>
    /// Creates a shorter hash for MongoDB index efficiency while maintaining uniqueness.
    /// Uses first 16 characters of SHA-256 hash.
    /// </summary>
    /// <param name="tenantId">The tenant identifier</param>
    /// <param name="idempotencyKey">The original idempotency key</param>
    /// <returns>Short hashed key (16 characters)</returns>
    public static string CreateShortHashedKey(string tenantId, string idempotencyKey)
    {
        var fullHash = CreateHashedKey(tenantId, idempotencyKey);
        return fullHash[..16]; // Take first 16 characters
    }
    
    /// <summary>
    /// Validates that a key appears to be a valid hash format.
    /// </summary>
    /// <param name="key">The key to validate</param>
    /// <returns>True if the key appears to be a valid hash</returns>
    public static bool IsValidHashFormat(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
            
        // Check if it's a valid hex string
        return key.All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f'));
    }
}
