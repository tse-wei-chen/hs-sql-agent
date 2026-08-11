namespace Admin.Service.Models;

public sealed class CustomSqlToolImpact
{
    public int ToolId { get; init; }
    public int? DraftDbManagementId { get; init; }
    public string? DraftDatabaseName { get; init; }
    public int? PublishedDbManagementId { get; init; }
    public string? PublishedDatabaseName { get; init; }
    public IReadOnlyList<CustomSqlToolImpactKey> CurrentlyExposedToKeys { get; init; } = [];
    public IReadOnlyList<CustomSqlToolImpactKey> WouldExposeToKeys { get; init; } = [];
    public IReadOnlyList<string> BreakingChanges { get; init; } = [];
    public bool SqlChanged { get; init; }
}

public sealed record CustomSqlToolImpactKey(int Id, string Name, string KeyPrefix);
