namespace Admin.Service.Models;

public class PermissionVM
{
	public string? UserName { get; set; }
	public string? Email { get; set; }
	public string? AccessToken { get; set; }
	public string? RefreshToken { get; set; }
	public string? ChangePasswordToken { get; set; }
}
