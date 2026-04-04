namespace Modules.Models;
public class SignInVM
{
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? ChangePasswordToken { get; set; }
}