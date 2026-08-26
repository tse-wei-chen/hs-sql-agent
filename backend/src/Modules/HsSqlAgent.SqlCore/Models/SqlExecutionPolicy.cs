namespace HsSqlAgent.SqlCore.Models;

public class SqlExecutionPolicy
{
    public int QueryMaxRows { get; set; }
    public int QueryTimeoutSeconds { get; set; } = 30;
    public bool RequireWhereForUpdate { get; set; }
    public bool RequireWhereForDelete { get; set; }
    public bool AllowFullTableUpdate { get; set; }
    public bool AllowFullTableDelete { get; set; }
    public int DmlMaxAffectedRows { get; set; }
}
