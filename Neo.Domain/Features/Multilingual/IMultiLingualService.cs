namespace Neo.Domain.Features.Multilingual;
public interface IMultiLingualService
{
    string GetMessage(string key);
    string GetMessage(string key, string enDefaultValue, string faDefaultValue);
    string GetMessage(string lang, string key, string enDefaultValue, string faDefaultValue);
}
