using System.ComponentModel.DataAnnotations;

namespace Auth.Service.Models;

public class SignUpRequest
{
    [Required]
    public required string Email { get; set; }

    [Required]
    public required string Password { get; set; }
}
