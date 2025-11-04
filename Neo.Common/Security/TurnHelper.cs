using System.Security.Cryptography;
using System.Text;

namespace Neo.Common.Security;

public class TurnCredential
{
    public string Username { get; set; } = "";
    public string Credential { get; set; } = "";
    public string Url { get; set; } = "";
}

public static class TurnHelper
{
    public static TurnCredential GenerateTurnCredential(string secret, string turnUrl, int minutes)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();
        var username = expiry.ToString();

        var keyBytes = Encoding.ASCII.GetBytes(secret);
        var usernameBytes = Encoding.ASCII.GetBytes(username);

        using var hmac = new HMACSHA1(keyBytes);
        var hash = hmac.ComputeHash(usernameBytes);
        var credential = Convert.ToBase64String(hash);

        return new TurnCredential
        {
            Username = username,
            Credential = credential,
            Url = $"turn:{turnUrl}?transport=udp"
        };
    }
}
