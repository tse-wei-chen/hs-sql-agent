namespace HsSqlAgent.SqlCore.Core.Pipeline;

public sealed record DmlCompilationPolicy(
    bool RequireWhereForUpdate = true,
    bool RequireWhereForDelete = true,
    bool AllowFullTableUpdate = false,
    bool AllowFullTableDelete = false);
