namespace Neo.Common.Extensions;

public static class FileTypeFinder
{
    public static FilePrimaryType GetPrimaryType(this string mimeType)
    {
        return string.IsNullOrEmpty(mimeType)
            ? FilePrimaryType.None
            : mimeType.StartsWith("image", StringComparison.CurrentCultureIgnoreCase)
            ? FilePrimaryType.Image
            : mimeType.StartsWith("video", StringComparison.CurrentCultureIgnoreCase)
            ? FilePrimaryType.Video
            : mimeType.StartsWith("audio", StringComparison.CurrentCultureIgnoreCase)
            ? FilePrimaryType.Audio
            : mimeType.StartsWith("text", StringComparison.CurrentCultureIgnoreCase)
            ? FilePrimaryType.Text
            : mimeType.ToLower() switch
            {
                "application/pdf" => FilePrimaryType.Pdf,
                "application/vnd.adobe.flash-movie" or "application/x-shockwave-flash" => FilePrimaryType.Swf,
                _ => FilePrimaryType.None,
            };
    }
}

public enum FilePrimaryType
{
    Image,
    Video,
    Text,
    Pdf,
    Swf, //adobe flash
    Audio,
    None
}
