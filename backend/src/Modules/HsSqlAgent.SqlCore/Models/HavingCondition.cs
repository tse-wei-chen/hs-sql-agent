using System.ComponentModel;
using System.Text.Json.Serialization;
using SqlAgent.Service.Enums;

namespace HsSqlAgent.SqlCore.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BasicHavingCondition), "basic")]
[JsonDerivedType(typeof(FunctionHavingCondition), "function_compare")]
[JsonDerivedType(typeof(ExpressionHavingCondition), "expression")]
[JsonDerivedType(typeof(GroupHavingCondition), "group")]
public abstract class HavingCondition
{
    private protected HavingCondition()
    {
        var type = GetType();
        if (type != typeof(BasicHavingCondition)
            && type != typeof(FunctionHavingCondition)
            && type != typeof(ExpressionHavingCondition)
            && type != typeof(GroupHavingCondition))
        {
            throw new InvalidOperationException(
                $"Unsupported HAVING node '{type.Name}'. Register compiler support before adding a new HAVING node type.");
        }
    }

    [Description("When true, this condition (or group) will be combined using OR instead of AND.")]
    public bool IsOr { get; set; }
    [Description("When true, negates the entire condition or group (NOT).")]
    public bool IsNot { get; set; }
}

public class BasicHavingCondition : HavingCondition
{
    [Description("FieldName to check in HAVING clause. e.g., 'total_amount' (if aliased or evaluated)")]
    public string FieldName { get; set; } = string.Empty;
    [Description("Comparison operator: '=', '>', '<', '>=', '<=', '<>'")]
    public string Operator { get; set; } = "=";
    [Description("The value to compare against.")]
    public object? Value { get; set; }
    [Description("Whether the value is a date.")]
    public bool IsDate { get; set; }
}

public class FunctionHavingCondition : HavingCondition
{
    [Description("The SQL Function being evaluated. e.g., SUM(o.total_price)")]
    public SqlFunctionCondition LeftFunction { get; set; } = new();

    [Description("Comparison operator, e.g., '>', '<=', '='")]
    public string Operator { get; set; } = ">";

    [Description("The expected threshold value, e.g., 50000")]
    public object? Value { get; set; }
}

public class ExpressionHavingCondition : HavingCondition
{
    [Description("The left-hand side expression (field, operation, function, etc.).")]
    public SelectCondition LeftExpression { get; set; } = null!;

    [Description("Comparison operator, e.g., '>', '<=', '=', 'IS'.")]
    public string Operator { get; set; } = ">";

    [Description("The optional right-hand side expression. Leave null for IS NULL / IS NOT NULL.")]
    public SelectCondition? RightExpression { get; set; }
}

public class GroupHavingCondition : HavingCondition
{
    [Description("Nested HAVING conditions grouped together inside parentheses.")]
    public List<HavingCondition> Groups { get; set; } = [];
}
