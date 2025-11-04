namespace Neo.Common.Utility;

public class PluralNoun
{
    Dictionary<string, string> _nonRegularPlurals = new()
        {
            { "child", "children" },
            { "man", "men" },
            { "woman", "women" },
            { "mouse", "mice" },
            { "tooth", "teeth" },
            { "goose", "geese" },
            { "ox", "oxen" },
            { "fish", "fish" }
        };
    public string Plural(string noun)
    {
        if (_nonRegularPlurals.TryGetValue(noun, out string? reult))
            return reult;
        string end = noun.Substring(noun.Length - 1, 1).ToLower();
        string beforEnd = noun.Substring(noun.Length - 2, 1).ToLower();
        string beforBeforEnd = noun.Substring(noun.Length - 3, 1).ToLower();
        const string Vowel = "uoiea";

        switch (end)
        {
            case "y" when !Vowel.Contains(beforEnd):
                return noun[..^1] + "ies";
            case "s":
            case "z":
            case "x":
                return noun + "es";
            case "f" when !Vowel.Contains(beforEnd) && !Vowel.Contains(beforBeforEnd):
                return noun[..^1] + "ves";
            case "e" when beforEnd == "f":
                return noun[..^2] + "ves";
            case "o" when !Vowel.Contains(beforEnd):
                return noun + "es";
            default:
                if (noun.EndsWith("sh") || noun.EndsWith("ch") || noun.EndsWith("zh"))
                    return noun + "es";
                return noun + "s";
        }
    }
}
