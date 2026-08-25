using System.ComponentModel;

namespace HsSqlAgent.SqlCore.Models;

public class NameValuePair
{
    [Description("The field or column name.")]
    public string FieldName { get; set; } = string.Empty;
    [Description("The value to insert or update. Can be a string, number, boolean, or null.")]
    public object? Value { get; set; }
}

