using System.ComponentModel.DataAnnotations;

namespace Auth.Service.Models;

public class ForgotPasswordRequest { [Required, EmailAddress] public required string Email { get; set; } }
public class ResetPasswordRequest
{
    [Required] public required string Token { get; set; }
    [Required, MinLength(8)] public required string NewPassword { get; set; }
}

public class PasswordResetSettings
{
    public string BaseUrl { get; set; } = "http://localhost:3000/reset-password";
    public int ExpirationMinutes { get; set; } = 30;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpEnableSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string SmtpFrom { get; set; } = string.Empty;
}
