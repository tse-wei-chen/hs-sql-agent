using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SqlAgent.Service.Models;

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(FieldGroupByCondition), "field")]
[JsonDerivedType(typeof(FunctionGroupByCondition), "function")]
public abstract class GroupByCondition
{
    private protected GroupByCondition() { }
}

public class FieldGroupByCondition : GroupByCondition
{
    [Description("Pure field name to group by. e.g., 'o.customer_id'")]
    public string FieldName { get; set; } = string.Empty;
}

public class FunctionGroupByCondition : GroupByCondition
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
