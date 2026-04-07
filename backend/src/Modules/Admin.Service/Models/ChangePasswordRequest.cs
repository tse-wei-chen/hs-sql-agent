using System.ComponentModel.DataAnnotations;

namespace Admin.Service.Models;

public class ChangePasswordRequest
{
    [Required]
    public required string CurrentPassword { get; set; }
    [Required]
    public required string NewPassword { get; set; }
}
