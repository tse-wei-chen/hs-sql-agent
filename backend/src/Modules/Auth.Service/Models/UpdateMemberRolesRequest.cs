namespace Auth.Service.Models;

public class UpdateMemberRolesRequest
{
    public IReadOnlyCollection<int> RoleIds { get; set; } = [];
}
