namespace Admin.Service.Models;

public class JwtSettings
{
	public string SecretKey { get; set; } = string.Empty;
	public string Issuer { get; set; } = string.Empty;
	public string Audience { get; set; } = string.Empty;
	public int AccessTokenExpirationMinutes { get; set; }
	public int RefreshTokenExpirationDays { get; set; }
	public int ChangePasswordTokenExpirationMinutes { get; set; }
}
