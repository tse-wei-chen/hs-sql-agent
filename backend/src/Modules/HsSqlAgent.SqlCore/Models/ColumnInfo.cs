namespace HsSqlAgent.SqlCore.Models;

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string Column { get => Name; set => Name = value; }
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsPrimaryKey { get; set; }
    public int? PrimaryKeyOrdinal { get; set; }

    public ColumnInfo() { }

    public ColumnInfo(
        string name,
        string type,
        bool isPrimaryKey = false,
        int? primaryKeyOrdinal = null)
    {
        Name = name;
        Type = type;
        IsPrimaryKey = isPrimaryKey;
        PrimaryKeyOrdinal = primaryKeyOrdinal;
    }
}
