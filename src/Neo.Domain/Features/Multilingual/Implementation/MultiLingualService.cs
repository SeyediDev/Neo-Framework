using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Client;
using Neo.Domain.Repository;

namespace Neo.Domain.Features.Multilingual.Implementation;

public class MultiLingualService(IRequesterUser user, IQueryRepository<CultureTerm, int> cultureTermRepository) : IMultiLingualService
{
    public string GetMessage(string key)
    {
        return key switch
        {
            "Required" => GetMessage(user.Lang!, key, "Is required.", "الزامی می باشد."),
            "OtpMessageTemplate" => GetMessage(user.Lang!, key, "Your OTP is {otp}.", "کد یکبار مصرف شما {otp} می باشد."),
            "OtpDeleteAccountMessageTemplate" => GetMessage(user.Lang!, key, "Your delete account OTP is {otp}.", "کد یکبار مصرف شما {otp} جهت حذف اکانت می باشد."),
            _ => GetMessage(user.Lang!, key, key, key),
        };
    }

    public string GetMessage(string key, string enDefaultValue, string faDefaultValue)
    {
        return GetMessage(user.Lang!, key, enDefaultValue, faDefaultValue);
    }

    public string GetMessage(string lang, string key, string enDefaultValue, string faDefaultValue)
    {
        var cultureTerm = cultureTermRepository.FirstOrDefaultAsync(x => x.Language.Name == lang &&
            x.SubjectTitle == "Resource" && x.SubjectId == 1 && x.SubjectField == key, default).Result;
        return cultureTerm != null
            ? cultureTerm.Term
            : lang switch
            {
                "en" => enDefaultValue,
                "fa" => faDefaultValue,
                _ => enDefaultValue,
            };
    }
}
