using System.ComponentModel;
using System.Text.Json.Serialization;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType
)]
[JsonDerivedType(typeof(FieldSelectCondition), "field")]
[JsonDerivedType(typeof(OperationSelectCondition), "operation")]
[JsonDerivedType(typeof(ConstantSelectCondition), "constant")]
[JsonDerivedType(typeof(FunctionSelectCondition), "function")]
[JsonDerivedType(typeof(CaseWhenSelectCondition), "case_when")]
[JsonDerivedType(typeof(SubQuerySelectCondition), "subquery")]
public abstract class SelectCondition
{
    [Description("Alias for the selected field (Optional). e.g., 'total_amount'")]
    public string? Alias { get; set; }
}

public class FieldSelectCondition : SelectCondition
{
    [Description(@"CRITICAL: Pure column reference ONLY.
    - ABSOLUTELY NO SQL functions (e.g., COUNT, SUM, ROUND), NO brackets, NO filter clauses, and NO math operators allowed here.
    - Allowed: 'o.customer_id', 'p.unit_price', '*'.
    - PROHIBITED: 'COUNT(*)' -> Use 'type': 'function' instead!")]
    public string FieldName { get; set; } = string.Empty;
}

public class OperationSelectCondition : SelectCondition
{
    [Description("The left operand. Supports all SelectCondition types.")]
    public SelectCondition Left { get; set; } = null!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description("Must be one of the enum values: 'Add', 'Subtract', 'Multiply', 'Divide'")]
    public ArithmeticOperator Operator { get; set; } = ArithmeticOperator.Add;

    [Description("The right operand. Supports all SelectCondition types.")]
    public SelectCondition Right { get; set; } = null!;
}

public class ConstantSelectCondition : SelectCondition
{
    [Description("Pure literal values ONLY (numbers, strings, booleans). NO SQL code/functions allowed here.")]
    public object Constant { get; set; } = string.Empty;
}

public class FunctionSelectCondition : SelectCondition
{
    [Description("SQL function name in UPPERCASE. e.g., 'COUNT', 'SUM', 'AVG', 'ROUND', 'NULLIF', 'COALESCE'.")]
    public string FunctionName { get; set; } = string.Empty;

    [Description("Ordered list of arguments. Each argument is a SelectCondition.")]
    public List<SelectCondition>? Arguments { get; set; }

    [Description("Optional. DISTINCT keyword inside function, e.g., COUNT(DISTINCT o.customer_id) -> set IsDistinct = true.")]
    public bool IsDistinct { get; set; }

    [Description("Optional. Filter clause for aggregate functions (e.g., COUNT(*) FILTER (WHERE ...)). Put the WHERE conditions here.")]
    public List<WhereCondition>? FilterWhereConditions { get; set; }
}

public class CaseWhenSelectCondition : SelectCondition
{
    [Description("Cases for CASE WHEN expression.")]
    public List<CaseWhenClause> CaseWhen { get; set; } = [];

    [Description("The default value for ELSE in a CASE expression (Optional).")]
    public object? ElseValue { get; set; }
}

public class SubQuerySelectCondition : SelectCondition
{
    [Description("The table name for this query definition. (use schema-qualified table name)")]
    public string TableName { get; set; } = string.Empty;
    [Description("The subquery to select from (Optional). If set, its results will be treated as the source table.")]
    public QueryDefinition? FromQuery { get; set; }
    [Description("Alias for the source table or subquery (Optional). CRITICAL: If you declare an alias here (e.g., 'p'), you MUST use exactly this alias prefix in all SelectColumns, Joins, and WhereConditions. Do not mix aliases! Example: set TableName='products' and Alias='p'.")]
    public new string? Alias { get; set; }
    [Description("When true, only returns unique rows.")]
    public bool Distinct { get; set; }
    [Description("List of columns to select.")]
    public List<SelectCondition>? SelectColumns { get; set; }
    [Description("Where conditions (supports nested logic and subqueries).")]
    public List<WhereCondition>? WhereColumnsAndValues { get; set; }
    [Description("Order by conditions.")]
    public List<OrderByCondition>? OrderByColumns { get; set; }
    [Description("Group by conditions.")]
    public List<GroupByCondition>? GroupByConditions { get; set; }
    [Description("Having conditions (supports nested logic).")]
    public List<HavingCondition>? HavingConditions { get; set; }
    [Description("Join conditions.")]
    public List<JoinCondition>? Joins { get; set; }
    [Description("Combine conditions (union, intersect, except).")]
    public List<CombineCondition>? CombineConditions { get; set; }
    [Description("CTE definitions.")]
    public List<CteCondition>? CteConditions { get; set; }
    [Description("Limit number of rows.")]
    public int? Limit { get; set; }
    [Description("Skip a number of rows (Optional).")]
    public int? Offset { get; set; }
}
