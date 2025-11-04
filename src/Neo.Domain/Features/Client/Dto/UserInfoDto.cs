namespace Neo.Domain.Features.Client.Dto;

public class UserInfoDto
{
    public required string sub { get; set; }
    public bool email_verified { get; set; }
    public required RealmAccess realm_access { get; set; }
    public int userid { get; set; }
    public required string username { get; set; }
}