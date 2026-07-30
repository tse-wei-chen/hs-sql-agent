using Admin.Service.Data.Entites;

namespace Admin.Service.Models;

public class SecurityPolicyModel
{
    public int QueryMaxRows { get; set; } = 1000;
    public int QueryTimeoutSeconds { get; set; } = 30;
    public bool RequireWhereForUpdate { get; set; } = true;
    public bool RequireWhereForDelete { get; set; } = true;
    public bool AllowFullTableUpdate { get; set; }
    public bool AllowFullTableDelete { get; set; }
    public int DmlMaxAffectedRows { get; set; } = 100;
    public int IpPermitLimit { get; set; } = 60;
    public int IpWindowSeconds { get; set; } = 60;
    public int KeyPermitLimit { get; set; } = 120;
    public int KeyWindowSeconds { get; set; } = 60;
    public int MaxConcurrentSql { get; set; } = 16;
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public SecurityPolicyModel Clone() => (SecurityPolicyModel)MemberwiseClone();

    public static SecurityPolicyModel FromEntity(SecurityPolicySettings entity) => new()
    {
        QueryMaxRows = entity.QueryMaxRows,
        QueryTimeoutSeconds = entity.QueryTimeoutSeconds,
        RequireWhereForUpdate = entity.RequireWhereForUpdate,
        RequireWhereForDelete = entity.RequireWhereForDelete,
        AllowFullTableUpdate = entity.AllowFullTableUpdate,
        AllowFullTableDelete = entity.AllowFullTableDelete,
        DmlMaxAffectedRows = entity.DmlMaxAffectedRows,
        IpPermitLimit = entity.IpPermitLimit,
        IpWindowSeconds = entity.IpWindowSeconds,
        KeyPermitLimit = entity.KeyPermitLimit,
        KeyWindowSeconds = entity.KeyWindowSeconds,
        MaxConcurrentSql = entity.MaxConcurrentSql,
        UpdatedAt = entity.UpdatedAt,
        UpdatedBy = entity.UpdatedBy
    };
}
