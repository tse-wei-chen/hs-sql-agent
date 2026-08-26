using System.ComponentModel;
using System.Text.Json.Serialization;

namespace HsSqlAgent.SqlCore.Models;

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization
)]
[JsonDerivedType(typeof(FieldSelectCondition), "field")]
[JsonDerivedType(typeof(OperationSelectCondition), "operation")]
[JsonDerivedType(typeof(ConstantSelectCondition), "constant")]
[JsonDerivedType(typeof(FunctionSelectCondition), "function")]
[JsonDerivedType(typeof(CaseWhenSelectCondition), "case_when")]
[JsonDerivedType(typeof(SubQuerySelectCondition), "subquery")]
[JsonDerivedType(typeof(CastSelectCondition), "cast")]
[JsonDerivedType(typeof(IntervalSelectCondition), "interval")]
public abstract class SelectCondition
{
    private protected SelectCondition() { }

    [Description("Alias for the selected field (Optional). e.g., 'total_amount'")]
    public string? Alias { get; set; }
}

public class FieldSelectCondition : SelectCondition
{
    [Description(@"CRITICAL: Pure column reference ONLY.
    - ABSOLUTELY NO SQL functions (e.g., COUNT, SUM, ROUND), NO brackets, NO filter clauses, and NO math operators allowed here.
    - Allowed: 'o.customer_id', 'p.unit_price', '*'.
    - PROHIBITED: 'COUNT(*)' -> Use 'type': 'function' instead!")]
    public string FieldName { get; init; } = string.Empty;
}

public class OperationSelectCondition : SelectCondition
{
    [Description("The left operand. Supports all SelectCondition types.")]
    public SelectCondition Left { get; init; } = null!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Description("Expression operator. Arithmetic, comparison, concatenation, and boolean operators are represented without changing their SQL semantics.")]
    public ArithmeticOperator Operator { get; init; } = ArithmeticOperator.Add;

    [Description("The right operand. Supports all SelectCondition types.")]
    public SelectCondition Right { get; init; } = null!;
}

public class ConstantSelectCondition : SelectCondition
{
    [Description("Pure literal values ONLY (numbers, strings, booleans). NO SQL code/functions allowed here.")]
    public object Constant { get; init; } = string.Empty;
}

public class CastSelectCondition : SelectCondition
{
    [Description("Expression being cast.")]
    public SelectCondition Expression { get; init; } = null!;

    [Description("Validated SQL type name, optionally including precision/scale.")]
    public string TypeName { get; init; } = string.Empty;
}

public class IntervalSelectCondition : SelectCondition
{
    [Description("Provider-native interval literal content without surrounding quotes, e.g. '1 day'.")]
    public string Literal { get; init; } = string.Empty;
}

internal class TemplateSqlTokenSelectCondition : SelectCondition
{
    public string Token { get; init; } = string.Empty;
}

internal class TemplateExtractSelectCondition : SelectCondition
{
    public SelectCondition Unit { get; init; } = null!;
    public SelectCondition Expression { get; init; } = null!;
}

internal class TemplateCaseSelectCondition : SelectCondition
{
    public List<TemplateCaseBranch> Cases { get; init; } = [];
    public SelectCondition? ElseExpression { get; init; }
}

internal class TemplateCaseBranch
{
    public SelectCondition Condition { get; init; } = null!;
    public SelectCondition Value { get; init; } = null!;
}

public class FunctionSelectCondition : SelectCondition
{
    [Description("SQL function name in UPPERCASE. e.g., 'COUNT', 'SUM', 'AVG', 'ROUND', 'NULLIF', 'COALESCE'.")]
    public string FunctionName { get; init; } = string.Empty;

    [Description("Ordered list of arguments. Each argument is a SelectCondition.")]
    public List<SelectCondition>? Arguments { get; init; }

    [Description("Optional. DISTINCT keyword inside function, e.g., COUNT(DISTINCT o.customer_id) -> set IsDistinct = true.")]
    public bool IsDistinct { get; init; }

    [Description("Optional. Filter clause for aggregate functions (e.g., COUNT(*) FILTER (WHERE ...)). Put the WHERE conditions here.")]
    public List<WhereCondition>? FilterWhereConditions { get; init; }

    [Description("Optional. Window definition for SELECT window functions, e.g., LAG(order_date) OVER (PARTITION BY customer_id ORDER BY order_date).")]
    public WindowDefinition? Window { get; init; }
}

public class CaseWhenSelectCondition : SelectCondition
{
    [Description("Cases for CASE WHEN expression.")]
    public List<CaseWhenClause> CaseWhen { get; init; } = [];

    [Description("The default value for ELSE in a CASE expression (Optional).")]
    public object? ElseValue { get; init; }
}

public class SubQuerySelectCondition : SelectCondition
{
    [Description("The table name for this query definition. (use schema-qualified table name)")]
    public string TableName { get; set; } = string.Empty;
    [Description("The subquery to select from (Optional). If set, its results will be treated as the source table.")]
    public QueryDefinition? FromQuery { get; set; }
    [Description("Alias for the source table/subquery and the scalar projection (Optional). CRITICAL: If you declare an alias here (e.g., 'p'), you MUST use exactly this alias prefix in all SelectColumns, Joins, and WhereConditions. Do not mix aliases!")]
    public new string? Alias
    {
        get => base.Alias;
        set => base.Alias = value;
    }
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
