using System.ComponentModel;

namespace ToolBox.Models;
public class SelectCondition
{
    [Description("The field name to select.")]
    public string Field { get; set; } = string.Empty;
    [Description("Alias for the selected field (Optional).")]
    public string Alias { get; set; } = string.Empty;
    [Description("Optional aggregation function (e.g., 'SUM', 'COUNT').")]
    public string Aggregation { get; set; } = string.Empty;
}

public class WhereCondition
{
    [Description("The field name to apply the condition on.")]
    public string Field { get; set; } = string.Empty;
    [Description("The operator to use in the condition. Legacy note: prefer InWhereConditions instead of using IN/NOT IN here.")]
    public string Operator { get; set; } = "=";
    [Description("The value to compare the field against.")]
    public object Value { get; set; } = string.Empty;
}

public class StringWhereCondition
{
    [Description("The field name to apply string matching on.")]
    public string Field { get; set; } = string.Empty;
    [Description("The value to match.")]
    public string Value { get; set; } = string.Empty;
    [Description("Match mode: contains, starts, ends, like.")]
    public string MatchMode { get; set; } = "contains";
    [Description("When true, use case-insensitive matching where supported (ILIKE).")]
    public bool CaseInsensitive { get; set; } = false;
}

public class DateWhereCondition
{
    [Description("The date/datetime field name to apply date comparison on.")]
    public string Field { get; set; } = string.Empty;
    [Description("The operator to use in date comparison, e.g. '=', '>', '>=', '<', '<='.")]
    public string Operator { get; set; } = "=";
    [Description("Date value in ISO format, e.g. '1997-01-01'.")]
    public string Value { get; set; } = string.Empty;
}

public class InWhereCondition
{
    [Description("The field name to apply IN/NOT IN on.")]
    public string Field { get; set; } = string.Empty;
    [Description("Collection values for IN/NOT IN condition.")]
    public List<object> Values { get; set; } = [];
    [Description("When true, apply NOT IN. Otherwise apply IN.")]
    public bool NotIn { get; set; } = false;
}

public class JoinCondition
{
    [Description("The table to join with (e.g., 'Orders')")]
    public string Table { get; set; } = string.Empty;

    [Description("First table and field for the ON condition (e.g., 'Users.Id')")]
    public string First { get; set; } = string.Empty;

    [Description("Second table and field for the ON condition (e.g., 'Orders.UserId')")]
    public string Second { get; set; } = string.Empty;

    [Description("The operator for the ON condition (e.g., '=')")]
    public string Operator { get; set; } = "=";

    [Description("INNER, LEFT, or RIGHT")]
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
    [Description("Aggregation function (Optional). Use 'SUM' to order by total, 'COUNT' for frequency.")]
    public string Aggregation { get; set; } = string.Empty;
    [Description("ASC or DESC")]
    public string Direction { get; set; } = "ASC";
}

public class HavingCondition
{
    [Description("The field name or expression to apply HAVING on.")]
    public string Field { get; set; } = string.Empty;
    [Description("The operator to use in HAVING condition.")]
    public string Operator { get; set; } = "=";
    [Description("The value to compare against in HAVING.")]
    public object Value { get; set; } = string.Empty;
    [Description("Optional aggregation function (e.g., SUM, COUNT).")]
    public string Aggregation { get; set; } = string.Empty;
}

public class QueryDefinition
{
    [Description("The table name for this query definition.")]
    public string TableName { get; set; } = string.Empty;
    [Description("List of columns to select.")]
    public List<SelectCondition>? SelectColumns { get; set; }
    [Description("Where conditions.")]
    public List<WhereCondition>? WhereColumnsAndValues { get; set; }
    [Description("Date-specific where conditions using SqlKata WhereDate.")]
    public List<DateWhereCondition>? DateWhereConditions { get; set; }
    [Description("IN/NOT IN specific where conditions.")]
    public List<InWhereCondition>? InWhereConditions { get; set; }
    [Description("String matching where conditions.")]
    public List<StringWhereCondition>? StringWhereConditions { get; set; }
    [Description("Order by conditions.")]
    public List<OrderByCondition>? OrderByColumns { get; set; }
    [Description("Group by conditions.")]
    public List<GroupByCondition>? GroupByConditions { get; set; }
    [Description("Having conditions.")]
    public List<HavingCondition>? HavingConditions { get; set; }
    [Description("Join conditions.")]
    public List<JoinCondition>? Joins { get; set; }
    [Description("Limit number of rows.")]
    public int? Limit { get; set; }
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