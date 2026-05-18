using System.ComponentModel;
using System.Text.Json.Serialization;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;

public class JoinCondition
{
    [Description("Pure table name only (e.g., 'Orders'). Leave empty if 'subQuery' is provided.")]
    public string Table { get; set; } = string.Empty;

    private QueryDefinition? _subQuery;
    [Description("The subquery to join. If set, 'Table' is ignored. e.g., SELECT ... FROM (SELECT ...) AS sub")]
    public QueryDefinition? SubQuery
    {
        get => (_subQuery != null && string.IsNullOrWhiteSpace(_subQuery.TableName) && _subQuery.FromQuery == null) ? null : _subQuery;
        set => _subQuery = value;
    }

    [Description("CRITICAL: Table alias. If defined (e.g. 'o'), you MUST use 'o.column_name' everywhere else.")]
    public string? Alias { get; set; }

    [Description("Join type: 'INNER', 'LEFT', 'RIGHT', 'FULL', 'CROSS'")]
    public JoinType Type { get; set; } = JoinType.Inner;

    [Description(@"CRITICAL: The ON join conditions. You MUST provide at least one condition.
    - Always use 'column_compare' for matching columns (e.g., o.id = od.order_id).")]
    public List<WhereCondition> OnConditions { get; set; } = new();
}
