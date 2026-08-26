using System.ComponentModel;
using System.Text.Json.Serialization;

namespace HsSqlAgent.SqlCore.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BasicWhereCondition), "basic")]
[JsonDerivedType(typeof(ColumnCompareWhereCondition), "column_compare")]
[JsonDerivedType(typeof(SubQueryWhereCondition), "subquery")]
[JsonDerivedType(typeof(GroupWhereCondition), "group")]
[JsonDerivedType(typeof(ExpressionWhereCondition), "expression")]
public abstract class WhereCondition
{
    private protected WhereCondition()
    {
        var type = GetType();
        if (type != typeof(BasicWhereCondition)
            && type != typeof(ColumnCompareWhereCondition)
            && type != typeof(SubQueryWhereCondition)
            && type != typeof(GroupWhereCondition)
            && type != typeof(ExpressionWhereCondition))
        {
            throw new InvalidOperationException(
                $"Unsupported WHERE node '{type.Name}'. Register compiler support before adding a new WHERE node type.");
        }
    }

    [Description("When true, this condition (or group) will be combined using OR instead of AND.")]
    public bool IsOr { get; set; }
    [Description("When true, negates the entire condition or group (NOT).")]
    public bool IsNot { get; set; }
}

public class BasicWhereCondition : WhereCondition
{
    [Description("The column name to filter. e.g., 'p.discontinued'")]
    public string FieldName { get; set; } = string.Empty;
    [Description("Comparison operator: '=', '>', '<', '>=', '<=', '<>', 'LIKE', 'ILIKE', 'IN', 'NOT IN'")]
    public string Operator { get; set; } = "=";
    [Description("The value to compare against (for IN/NOT IN, use Values array instead).")]
    public object? Value { get; set; }
    [Description("The values array for IN/NOT IN operator. When set, Operator should be 'IN' or 'NOT IN'.")]
    public List<object> Values { get; set; } = [];
    [Description("Whether the value is a date.")]
    public bool IsDate { get; set; }
}

public class ColumnCompareWhereCondition : WhereCondition
{
    [Description("The left-hand side column reference. e.g., 'c.customer_id'")]
    public string LeftFieldName { get; set; } = string.Empty;

    [Description("The operator, typically '='")]
    public string Operator { get; set; } = "=";

    [Description("The right-hand side column reference. e.g., 'o.customer_id'")]
    public string RightFieldName { get; set; } = string.Empty;
}

public class ExpressionWhereCondition : WhereCondition
{
    [Description("The left-hand side expression (field, operation, function, etc.).")]
    public SelectCondition LeftExpression { get; set; } = null!;
    [Description("Comparison operator: '=', '>', '<', '>=', '<=', '<>', 'LIKE', 'ILIKE'")]
    public string Operator { get; set; } = "=";
    [Description("The optional right-hand side expression. If set, Value is ignored.")]
    public SelectCondition? RightExpression { get; set; }
}

public class SubQueryWhereCondition : WhereCondition
{
    [Description("The field name for IN/NOT IN with subquery. Leave null/empty ONLY for 'EXISTS' or 'NOT EXISTS'.")]
    public string? FieldName { get; set; }
    [Description("Operator: 'IN', 'NOT IN', 'EXISTS', 'NOT EXISTS'")]
    public string Operator { get; set; } = "IN";
    [Description("The subquery to use for the filter.")]
    public QueryDefinition SubQuery { get; set; } = new();
}

public class GroupWhereCondition : WhereCondition
{
    [Description("Nested conditions grouped together inside parentheses.")]
    public List<WhereCondition> Groups { get; set; } = [];
}
