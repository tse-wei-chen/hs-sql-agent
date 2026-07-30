namespace Admin.Service.Data.Entites;

public class SecurityPolicySettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public int QueryMaxRows { get; set; } = 1000;
    public int QueryTimeoutSeconds { get; set; } = 30;
    public bool RequireWhereForUpdate { get; set; } = true;
    public bool RequireWhereForDelete { get; set; } = true;
    public bool AllowFullTableUpdate { get; set; }
    public bool AllowFullTableDelete { get; set; }
    public int DmlMaxAffectedRows { get; set; } = 100;
    public int KeyPermitLimit { get; set; } = 120;
    public int KeyWindowSeconds { get; set; } = 60;
    public int MaxConcurrentSql { get; set; } = 16;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}
