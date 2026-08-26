using System.ComponentModel;
using System.Text.Json.Serialization;
using HsSqlAgent.SqlCore.Enums;

namespace HsSqlAgent.SqlCore.Models;

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(FieldOrderByCondition), "field")]
[JsonDerivedType(typeof(FunctionOrderByCondition), "function")]
public abstract class OrderByCondition
{
    private protected OrderByCondition() { }

    [Description("'asc' or 'desc'")]
    public SortDirection Direction { get; set; } = SortDirection.Asc;

    [Description("Optional NULL ordering. Default preserves provider behavior.")]
    public NullOrdering NullOrdering { get; set; } = NullOrdering.Default;
}

public class FieldOrderByCondition : OrderByCondition
{
    [Description("The field name to sort by. e.g., 'total_amount'")]
    public string FieldName { get; set; } = string.Empty;
}

public class FunctionOrderByCondition : OrderByCondition
{
    [Description("SQL function name in UPPERCASE. e.g., 'COUNT', 'SUM', 'ROUND', 'NULLIF', 'COALESCE'.")]
    public string FunctionName { get; set; } = string.Empty;

    [Description("Ordered list of arguments. Each argument is a SelectCondition.")]
    public List<SelectCondition>? Arguments { get; set; }

    [Description("Optional. DISTINCT keyword inside function, e.g., COUNT(DISTINCT o.customer_id) -> set IsDistinct = true.")]
    public bool IsDistinct { get; set; }

    [Description("Optional. Filter clause for aggregate functions (e.g., COUNT(*) FILTER (WHERE ...)). Put the WHERE conditions here.")]
    public List<WhereCondition>? FilterWhereConditions { get; set; }
}
