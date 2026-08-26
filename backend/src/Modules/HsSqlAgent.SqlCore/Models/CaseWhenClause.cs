using System.ComponentModel;

namespace HsSqlAgent.SqlCore.Models;

public class CaseWhenClause
{
    [Description("The condition for WHEN.")]
    public WhereCondition Condition { get; set; } = null!;
    [Description("The value for THEN.")]
    public object Value { get; set; } = string.Empty;
}
