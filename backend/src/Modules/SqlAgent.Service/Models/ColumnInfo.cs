namespace SqlAgent.Service.Models;

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string Column { get => Name; set => Name = value; }
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public ColumnInfo() { }

    public ColumnInfo(string name, string type)
    {
        Name = name;
        Type = type;
    }
}
