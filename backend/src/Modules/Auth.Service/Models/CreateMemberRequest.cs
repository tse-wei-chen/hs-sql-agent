using System.ComponentModel.DataAnnotations;

namespace Auth.Service.Models;

public class CreateMemberRequest
{
    [Required]
    public required string Email { get; set; }

    public string? Username { get; set; }

    [Required]
    public required string Password { get; set; }

    public bool AssignAllRoles { get; set; }

    public IReadOnlyCollection<int> RoleIds { get; set; } = [];
}
