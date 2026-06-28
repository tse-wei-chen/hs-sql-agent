namespace Auth.Service.Data.Entites;

public class MemberRole
{
    public int MemberId { get; set; }

    public int RoleId { get; set; }    
    public Member Member { get; set; } = null!;
    public Role Role { get; set; } = null!;
}
