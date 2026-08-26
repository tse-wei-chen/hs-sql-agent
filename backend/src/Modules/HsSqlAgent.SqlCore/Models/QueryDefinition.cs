using System.ComponentModel;
using System.Text.Json.Serialization;

namespace HsSqlAgent.SqlCore.Models;

public class QueryDefinition
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description(@"Dialect used by all input function names and format strings.
    - Set this whenever the query uses syntax from a specific SQL dialect.
    - If omitted, the input is declared to already use the target provider dialect.
    - Omission does NOT enable dialect detection or best-effort guessing; syntax from another dialect is rejected.")]
    public SqlAgentToolType? SourceDialect { get; set; }

    [Description("The table name for this query definition. (use schema-qualified table name)")]
    public string TableName { get; set; } = string.Empty;
    [Description("The subquery to select from (Optional). If set, its results will be treated as the source table.")]
    public QueryDefinition? FromQuery { get; set; }
    [Description("Alias for the source table or subquery (Optional). CRITICAL: If you declare an alias here (e.g., 'p'), you MUST use exactly this alias prefix in all SelectColumns, Joins, and WhereConditions. Do not mix aliases! Example: set TableName='products' and Alias='p'.")]
    public string? Alias { get; set; }
    [Description("When true, only returns unique rows.")]
    public bool Distinct { get; set; }
    [Description("List of columns to select.")]
    public List<SelectCondition>? SelectColumns { get; set; }
    [Description("Where conditions (supports nested logic and subqueries).")]
    public List<WhereCondition>? WhereColumnsAndValues { get; set; }
    [Description("Order by conditions.")]
    public List<OrderByCondition>? OrderByColumns { get; set; }
    [Description("Group by conditions.")]
    public List<GroupByCondition>? GroupByConditions { get; set; }
    [Description("Having conditions (supports nested logic).")]
    public List<HavingCondition>? HavingConditions { get; set; }
    [Description("Join conditions.")]
    public List<JoinCondition>? Joins { get; set; }
    [Description("Combine conditions (union, intersect, except).")]
    public List<CombineCondition>? CombineConditions { get; set; }
    [Description("CTE definitions.")]
    public List<CteCondition>? CteConditions { get; set; }
    [Description("Limit number of rows.")]
    public int? Limit { get; set; }
    [Description("Skip a number of rows (Optional).")]
    public int? Offset { get; set; }
}
