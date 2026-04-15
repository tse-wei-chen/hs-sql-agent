namespace Admin.Service.Models;

public class RateLimitingSettings
{
	public int PermitLimit { get; set; }
	public int WindowSeconds { get; set; }
	public int QueueLimit { get; set; }
}
