using System.ComponentModel.DataAnnotations;

namespace Auth.Service.Models;

public class UpdateAccountRequest
{
    [Required, MaxLength(100)] public required string Username { get; set; }
    [Required, EmailAddress, MaxLength(320)] public required string Email { get; set; }
}

public class ChangePasswordRequest
{
    [Required] public required string CurrentPassword { get; set; }
    [Required, MinLength(8)] public required string NewPassword { get; set; }
}

public class RequirePasswordResetRequest
{
    public bool Required { get; set; } = true;
}
