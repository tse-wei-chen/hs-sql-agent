namespace Auth.Service.Models;

public class JwtSettings
{
    public string SecretKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "HS-Agent";
    public string Audience { get; set; } = "HS-Agent-Users";
    public int AccessTokenExpirationMinutes { get; set; } = 1;
    public int RefreshTokenExpirationDays { get; set; } = 30;
    public int SignInLockoutThreshold { get; set; } = 5;
    public int SignInLockoutMinutes { get; set; } = 15;
}
