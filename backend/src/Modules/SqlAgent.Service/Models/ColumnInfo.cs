namespace SqlAgent.Service.Models;

public class ColumnInfo
{
    public string Name { get; set; }
    public string Column { get => Name; set => Name = value; }
    public string Type { get; set; }
    public string Description { get; set; }

    public ColumnInfo() { }

    public ColumnInfo(string name, string type)
    {
        Name = name;
        Type = type;
    }
}
