using System.ComponentModel;

namespace SqlAgent.Service.Models;

public class CteCondition
{
    [Description("CTE alias name.")]
    public string CteAliasName { get; set; } = string.Empty;
    [Description("Query definition to combine with.")]
    public QueryDefinition Query { get; set; } = new();
}
