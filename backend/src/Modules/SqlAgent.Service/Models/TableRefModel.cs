namespace SqlAgent.Service.Models;

public class TableRefModel
{
    public string? SourceTable { get; set; }
    public string? ForeignKey { get; set; }
    public string? ReferenceTable { get; set; }
    public string? PrimaryKey { get; set; }
}
