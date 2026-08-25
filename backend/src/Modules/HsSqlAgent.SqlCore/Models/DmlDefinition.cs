using System.ComponentModel;
using SqlAgent.Service.Enums;

namespace HsSqlAgent.SqlCore.Models;

public class DmlDefinition
{
    [Description("'insert', 'update', or 'delete'")]
    public DmlOperation Operation { get; set; } = DmlOperation.Insert;
    [Description("The table name to operate on. (use schema-qualified table name)")]
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
