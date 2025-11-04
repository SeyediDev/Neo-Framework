using System.Text;
using Neo.Common.Extensions;

namespace Neo.Common.Security;
public class JwtDecode : IJwtDecode
{
    public TPayload FetchPayload<TPayload>(string jwt)
    {
        if (string.IsNullOrEmpty(jwt))
        {
            throw new ArgumentNullException(nameof(jwt));
        }

        byte[] data = Base64DecodeAsByte(jwt.Split('.')[1]);
        string payload = Encoding.UTF8.GetString(data);
        return payload.FromJson<TPayload>();
    }

    private byte[] Base64DecodeAsByte(string input)
    {
        string output = input;
        output = output.Replace('-', '+'); // 62nd char of encoding
        output = output.Replace('_', '/'); // 63rd char of encoding
        switch (output.Length % 4) // Pad with trailing '='s
        {
            case 0: break; // No pad chars in this case
            case 2: output += "=="; break; // Two pad chars
            case 3: output += "="; break; // One pad char
            default: throw new Exception("Illegal base64url string!");
        }
        byte[] base64Bytes = Convert.FromBase64String(output);
        return base64Bytes;
    }
}
