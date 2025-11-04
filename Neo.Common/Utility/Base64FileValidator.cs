namespace Neo.Common.Utility;

public static class Base64FileValidator
{
    public static (bool IsValid, byte[]? fileBytes, string? mimeType, string ErrorMessage) ValidateBase64File(string base64String, int maxSizeInMegabyte = 5)
    {
        if (string.IsNullOrWhiteSpace(base64String))
            return (false,null,null, "Input is empty");

        if (!IsBase64String(base64String))
            return (false, null, null, "Invalid Base64 format");

        byte[] fileBytes;
        try
        {
            fileBytes = GetFileBytes(base64String);
        }
        catch
        {
            return (false, null, null, "Decoding Base64 failed");
        }

        if (!IsFileSizeValid(fileBytes, maxSizeInMegabyte))
            return (false, null, null, "File too large");

        string mimeType = CandoMimeTypes.GetMimeType(fileBytes); 
        if (mimeType == "application/octet-stream")
            return (false, null, null, "Unsupported file type");

        return (true,fileBytes,mimeType, "Valid file");
    }

    private static bool IsBase64String(string base64)
    {
        Span<byte> buffer = new Span<byte>(new byte[base64.Length]);
        return Convert.TryFromBase64String(base64, buffer, out _);
    }

    private static byte[] GetFileBytes(string base64)
    {
        return Convert.FromBase64String(base64);
    }

    private static bool IsFileSizeValid(byte[] fileBytes, int maxSizeInBytes)
    {
        return fileBytes.Length <= maxSizeInBytes * 1024 * 1024;
    }
}
