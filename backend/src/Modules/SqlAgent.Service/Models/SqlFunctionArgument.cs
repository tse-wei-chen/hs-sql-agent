using System.ComponentModel;
using System.Text.Json.Serialization;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FieldFunctionArgument), "field")]
[JsonDerivedType(typeof(ConstantFunctionArgument), "constant")]
[JsonDerivedType(typeof(NestedFunctionArgument), "function")]
[JsonDerivedType(typeof(ArithmeticFunctionArgument), "operation")]
[JsonDerivedType(typeof(CaseWhenFunctionArgument), "case_when")]
public abstract class SqlFunctionArgument { }

public class FieldFunctionArgument : SqlFunctionArgument
{
    [Description("Pure column reference. Use '*' exclusively for COUNT(*).")]
    public string FieldName { get; set; } = string.Empty;
}

public class ConstantFunctionArgument : SqlFunctionArgument
{
    [Description("Pure literal value. Do NOT put raw SQL logic or function code here.")]
    public object Constant { get; set; } = null!;
}

public class NestedFunctionArgument : SqlFunctionArgument
{
    [Description("SQL function name in UPPERCASE. e.g., 'COUNT', 'SUM', 'ROUND', 'NULLIF', 'COALESCE'.")]
    public string FunctionName { get; set; } = string.Empty;

    [Description("Ordered list of arguments. Each argument must specify its own polymorphic type.")]
    public List<SqlFunctionArgument>? Arguments { get; set; }

    [Description("Optional. DISTINCT keyword inside function, e.g., COUNT(DISTINCT o.customer_id) -> set IsDistinct = true.")]
    public bool IsDistinct { get; set; }

    [Description("Optional. Filter clause for aggregate functions (e.g., COUNT(*) FILTER (WHERE ...)). Put the WHERE conditions here.")]
    public List<WhereCondition>? FilterWhereConditions { get; set; }
}

public class ArithmeticFunctionArgument : SqlFunctionArgument
{
    [Description("The left operand. Can be a field, constant, function, or another nested operation.")]
    public SelectArithmeticCondition Left { get; set; } = null!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description("Arithmetic operator: 'Add', 'Subtract', 'Multiply', 'Divide'")]
    public ArithmeticOperator Operator { get; set; } = ArithmeticOperator.Add;

    [Description("The right operand. Can be a field, constant, function, or another nested operation.")]
    public SelectArithmeticCondition Right { get; set; } = null!;
}

public class CaseWhenFunctionArgument : SqlFunctionArgument
{
    [Description("Cases for CASE WHEN expression.")]
    public List<CaseWhenClause> CaseWhen { get; set; } = [];

    [Description("The default value for ELSE in a CASE expression (Optional).")]
    public object? ElseValue { get; set; }
}
