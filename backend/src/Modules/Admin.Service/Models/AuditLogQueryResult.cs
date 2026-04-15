namespace Admin.Service.Models;

public class AuditLogQueryResult
{
	public List<AuditLogItem> Items { get; set; } = [];
	public int Page { get; set; }
	public int PageSize { get; set; }
	public int TotalCount { get; set; }
}

public class AuditLogItem
{
	public long Id { get; set; }
	public string ActorType { get; set; } = null!;
	public string? ActorId { get; set; }
	public string Action { get; set; } = null!;
	public string Target { get; set; } = null!;
	public string? Detail { get; set; }
	public string Result { get; set; } = null!;
	public string? IpAddress { get; set; }
	public string? UserAgent { get; set; }
	public DateTime CreatedAt { get; set; }
}
