namespace Admin.Service.Models;

public class AuditDailySummaryItem
{
	public DateTime Day { get; set; }
	public int SuccessCount { get; set; }
	public int FailedCount { get; set; }
}
