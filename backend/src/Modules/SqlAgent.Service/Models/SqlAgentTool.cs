using System.ComponentModel;

namespace SqlAgent.Service.Models;

public class SelectCondition
{
    [Description("The field name to select (if not using arithmetic or CASE).")]
    public string Field { get; set; } = string.Empty;
    [Description("Alias for the selected field (Optional).")]
    public string Alias { get; set; } = string.Empty;
    [Description("The aggregation function to apply (e.g., 'SUM', 'COUNT').")]
    public string Aggregation { get; set; } = string.Empty;
    [Description("Arithmetic expression (Optional). If set, 'Field' will be ignored.")]
    public SelectArithmeticCondition? Arithmetic { get; set; }
    [Description("Cases for CASE WHEN expression (Optional).")]
    public List<CaseWhenClause>? CaseWhen { get; set; }
    [Description("The default value for ELSE in a CASE expression (Optional).")]
    public object? ElseValue { get; set; }
    [Description("Subquery to select as a column (Optional). If set, 'Field' and 'Arithmetic' will be ignored.")]
    public QueryDefinition? SubQuery { get; set; }
}

public class CaseWhenClause
{
    [Description("The condition for WHEN.")]
    public WhereCondition Condition { get; set; } = new();
    [Description("The value for THEN.")]
    public object Value { get; set; } = string.Empty;
}

public class SelectArithmeticCondition
{
    [Description("The field name for this node (if it's a leaf node).")]
    public string? FieldName { get; set; }
    [Description("The constant value (if it's a constant node).")]
    public object? Constant { get; set; }

    [Description("The left operand for arithmetic.")]
    public SelectArithmeticCondition? Left { get; set; }
    [Description("The operator (+, -, *, /).")]
    public string? Operator { get; set; }
    [Description("The right operand for arithmetic.")]
    public SelectArithmeticCondition? Right { get; set; }
}

public class WhereCondition
{
    [Description("The field name to apply the condition on.")]
    public string Field { get; set; } = string.Empty;
    [Description("The operator to use in the condition (e.g., '=', '>', 'IN', 'EXISTS').")]
    public string Operator { get; set; } = "=";
    [Description("The value to compare the field against.")]
    public object? Value { get; set; }

    [Description("When true, this condition (or group) will be combined using OR instead of AND.")]
    public bool IsOr { get; set; }
    [Description("When true, negates the entire condition or group (NOT).")]
    public bool IsNot { get; set; }
    [Description("Recursive nested conditions. If provided, 'Field/Operator/Value' are ignored for this node.")]
    public List<WhereCondition>? Groups { get; set; }
    [Description("When true, treats the field and value as dates (obsoletes DateWhereCondition).")]
    public bool IsDate { get; set; }
    [Description("Subquery definition for use with 'EXISTS', 'NOT EXISTS', or 'IN' operators.")]
    public QueryDefinition? SubQuery { get; set; }
    [Description("List of values for 'IN' or 'NOT IN' operators.")]
    public List<object>? Values { get; set; }
}


public class JoinCondition
{
    [Description("The table to join with (e.g., 'Orders')")]
    public string Table { get; set; } = string.Empty;

    [Description("The subquery to join with (Optional). If set, 'Table' will be ignored.")]
    public QueryDefinition? SubQuery { get; set; }

    [Description("Alias for the joined table or subquery (Optional).")]
    public string? Alias { get; set; }

    [Description("First table and field for the ON condition (e.g., 'Users.Id')")]
    public string First { get; set; } = string.Empty;

    [Description("Second table and field for the ON condition (e.g., 'Orders.UserId')")]
    public string Second { get; set; } = string.Empty;

    [Description("The operator for the ON condition (e.g., '=')")]
    public string Operator { get; set; } = "=";

    [Description("Recursive nested ON conditions. If provided, First/Second/Operator are ignored.")]
    public List<WhereCondition>? OnConditions { get; set; }

    [Description("INNER, LEFT, RIGHT, or FULL")]
    public string Type { get; set; } = "INNER";
}

public class GroupByCondition
{
    [Description("The field to group by, format: 'TableName.FieldName' or 'FieldName'")]
    public string Field { get; set; } = string.Empty;
}

public class OrderByCondition
{
    [Description("The field name to order by (e.g., 'Products.Price' or 'Orders.TotalAmount').")]
    public string Field { get; set; } = string.Empty;
    [Description("asc or desc or random, default is asc")]
    public string Direction { get; set; } = "asc";
}

public class HavingCondition
{
    [Description("The field name or expression to apply HAVING on.")]
    public string Field { get; set; } = string.Empty;
    [Description("The operator to use in HAVING condition.")]
    public string Operator { get; set; } = "=";
    [Description("The value to compare against in HAVING.")]
    public object? Value { get; set; }
    [Description("Optional aggregation function (e.g., SUM, COUNT).")]
    public string Aggregation { get; set; } = string.Empty;

    [Description("When true, this condition (or group) will be combined using OR instead of AND.")]
    public bool IsOr { get; set; }
    [Description("When true, negates the entire condition or group (NOT).")]
    public bool IsNot { get; set; }
    [Description("Recursive nested conditions.")]
    public List<HavingCondition>? Groups { get; set; }
    [Description("When true, treats the field and value as dates.")]
    public bool IsDate { get; set; }
}

public class QueryDefinition
{
    [Description("The table name for this query definition.")]
    public string TableName { get; set; } = string.Empty;
    [Description("The subquery to select from (Optional). If set, its results will be treated as the source table.")]
    public QueryDefinition? FromQuery { get; set; }
    [Description("Alias for the source table or subquery (Optional).")]
    public string? Alias { get; set; }
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

public class CteCondition
{
    [Description("CTE alias name.")]
    public string Name { get; set; } = string.Empty;
    [Description("CTE query definition.")]
    public QueryDefinition Query { get; set; } = new();
}

public class CombineCondition
{
    [Description("Combine type: union, union all, intersect, except.")]
    public string Type { get; set; } = "union";
    [Description("Query definition to combine with.")]
    public QueryDefinition Query { get; set; } = new();
}

public class NameValuePair
{
    [Description("The field or column name.")]
    public string Name { get; set; } = string.Empty;
    [Description("The value to assign.")]
    public object? Value { get; set; }
}

public class DmlDefinition
{
    [Description("The operation to perform: insert, update, or delete.")]
    public string Operation { get; set; } = "insert";
    [Description("The table name to operate on.")]
    public string TableName { get; set; } = string.Empty;
    [Description("Where conditions for update or delete.")]
    public List<WhereCondition>? WhereConditions { get; set; }
    [Description("Values for insert or update.")]
    public List<NameValuePair>? Values { get; set; }
    [Description("Columns for bulk insert.")]
    public List<string>? Columns { get; set; }
    [Description("Multi-row values for bulk insert.")]
    public List<List<object>>? MultiValues { get; set; }
    [Description("Source query for INSERT INTO ... SELECT (Optional).")]
    public QueryDefinition? FromQuery { get; set; }
    [Description("A confirmation token required for potentially dangerous operations (Optional).")]
    public string? ConfirmToken { get; set; }
}
