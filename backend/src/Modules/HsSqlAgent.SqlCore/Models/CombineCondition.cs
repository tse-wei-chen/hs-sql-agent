using System.ComponentModel;

namespace HsSqlAgent.SqlCore.Models;

public class CombineCondition
{
    [Description("Combine type: union, union all, intersect, except.")]
    public CombineType Type { get; set; } = CombineType.Union;
    [Description("Query definition to combine with.")]
    public QueryDefinition Query { get; set; } = new();
}
