using System.ComponentModel.DataAnnotations;

namespace Auth.Service.Models;

public class RolePayload
{
    [Required]
    public required string Name { get; set; }

    public string? Description { get; set; }

    public IReadOnlyCollection<PermissionActionSelection> PermissionActions { get; set; } = [];
}
