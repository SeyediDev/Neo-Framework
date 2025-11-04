namespace Neo.Common.Security;

public interface IJwtDecode
{
    TDecodeToken FetchPayload<TDecodeToken>(string jwt);
}