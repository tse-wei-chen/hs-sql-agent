using System.ComponentModel;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Models;

public class WindowDefinition
{
    [Description("PARTITION BY columns (list of GroupByConditions, supports field and function types).")]
    public List<GroupByCondition>? PartitionBy { get; set; }

    [Description("ORDER BY columns inside the OVER clause.")]
    public List<OrderByCondition>? OrderBy { get; set; }
}
