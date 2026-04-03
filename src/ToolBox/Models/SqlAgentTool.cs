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
    [Description("The operator to use in the condition.")]
    public string Operator { get; set; } = "=";
    [Description("The value to compare the field against.")]
    public object Value { get; set; } = string.Empty;
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