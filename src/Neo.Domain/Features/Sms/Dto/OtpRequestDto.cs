namespace Neo.Domain.Features.Sms.Dto;
public class OtpRequestDto
{
    public string Mobile { get; set; } = null!;
    public int TemplateId { get; set; }
    public List<ParametersDto> Parameters { get; set; } = null!;
}

public class ParametersDto
{
    public string Name { get; set; } = null!;
    public string Value { get; set; } = null!;
}
