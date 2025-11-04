using System.Security.Cryptography;
using System.Text;

namespace Neo.Common.Extensions;

public static class HashingHelper
{
    public static string Sha256(this string rawData)
    {
        // Create a SHA256
        using var sha256Hash = SHA256.Create();
        // ComputeHash - returns byte array  
        var bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));

        // Convert byte array to a string   
        var builder = new StringBuilder();
        foreach (var t in bytes)
        {
            builder.Append(t.ToString("x2"));
        }

        return builder.ToString();
    }

    public static (string hashedString, string salt) Sha256WithSalt(this string rawData, string salt,
        bool useRandomSalt = false)
    {
        // Create a SHA256
        using var sha256Hash = SHA256.Create();
        // ComputeHash - returns byte array  

        if (useRandomSalt || salt.HasNotValue())
            salt = PasswordHelper.GeneratePassword(rawData.Length);

        var bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes($"{rawData}{salt}"));

        // Convert byte array to a string   
        var builder = new StringBuilder();
        foreach (var t in bytes)
        {
            builder.Append(t.ToString("x2"));
        }

        return (builder.ToString(), salt);
    }
}