using System.ComponentModel;
using System.Text.Json.Serialization;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FieldArithmeticCondition), "field")]
[JsonDerivedType(typeof(ConstantArithmeticCondition), "constant")]
[JsonDerivedType(typeof(OperationArithmeticCondition), "operation")]
[JsonDerivedType(typeof(FunctionArithmeticCondition), "function")]
[JsonDerivedType(typeof(CaseWhenArithmeticCondition), "case_when")]
public abstract class SelectArithmeticCondition
{
}

public class FieldArithmeticCondition : SelectArithmeticCondition
{
    [Description("Column name reference inside an arithmetic expression. e.g., 'price'")]
    public string FieldName { get; set; } = string.Empty;
}

public class ConstantArithmeticCondition : SelectArithmeticCondition
{
    [Description(@"CRITICAL: Pure literal values ONLY.
    - Allowed: 100.0, 2, 'active', false.
    - STRICTLY FORBIDDEN: Do NOT put raw SQL expressions, casts, or function calls inside this constant field! (e.g., NO 'ROUND(...)', NO 'COUNT(*)').")]
    public object Constant { get; set; } = string.Empty;
}

public class OperationArithmeticCondition : SelectArithmeticCondition
{
    [Description("The left operand.")]
    public SelectArithmeticCondition Left { get; set; } = null!;
    [Description("Must be one of the enum values: 'Add', 'Subtract', 'Multiply', 'Divide'")]
    public ArithmeticOperator Operator { get; set; } = ArithmeticOperator.Add;
    [Description("The right operand.")]
    public SelectArithmeticCondition Right { get; set; } = null!;
}

public class FunctionArithmeticCondition : SelectArithmeticCondition
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

public class CaseWhenArithmeticCondition : SelectArithmeticCondition
{
    [Description("CASE WHEN expression inside a math expression.")]
    public List<CaseWhenClause> CaseWhen { get; set; } = [];
    [Description("The default value for ELSE in a CASE expression (Optional).")]
    public object? ElseValue { get; set; }
}
