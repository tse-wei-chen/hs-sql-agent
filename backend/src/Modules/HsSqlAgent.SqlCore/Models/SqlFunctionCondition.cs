using System.ComponentModel;
using System.Text.Json.Serialization;
using HsSqlAgent.SqlCore.Enums;

namespace HsSqlAgent.SqlCore.Models;

public class SqlFunctionCondition
{
    [Description("SQL function name in UPPERCASE. e.g., 'COUNT', 'SUM', 'ROUND', 'NULLIF', 'COALESCE'.")]
    public string FunctionName { get; set; } = string.Empty;

    [Description("Ordered list of arguments. Each argument is a SelectCondition.")]
    public List<SelectCondition>? Arguments { get; set; }

    [Description("Optional. DISTINCT keyword inside function, e.g., COUNT(DISTINCT o.customer_id) -> set IsDistinct = true.")]
    public bool IsDistinct { get; set; }

    [Description("Optional. Filter clause for aggregate functions (e.g., COUNT(*) FILTER (WHERE ...)). Put the WHERE conditions here.")]
    public List<WhereCondition>? FilterWhereConditions { get; set; }

    [Description("Optional. Window definition for window functions (e.g., ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...)).")]
    public WindowDefinition? Window { get; set; }
}
