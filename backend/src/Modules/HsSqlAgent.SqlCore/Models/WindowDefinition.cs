using System.ComponentModel;

namespace HsSqlAgent.SqlCore.Models;

public class WindowDefinition
{
    [Description("PARTITION BY columns (list of GroupByConditions, supports field and function types).")]
    public List<GroupByCondition>? PartitionBy { get; set; }

    [Description("ORDER BY columns inside the OVER clause.")]
    public List<OrderByCondition>? OrderBy { get; set; }

    [Description("Optional ROWS or RANGE frame. The parser preserves both bounds.")]
    public WindowFrameDefinition? Frame { get; set; }
}

public class WindowFrameDefinition
{
    public WindowFrameUnit Unit { get; set; }
    public WindowFrameBound Start { get; set; } = new();
    public WindowFrameBound? End { get; set; }
}

public class WindowFrameBound
{
    public WindowFrameBoundKind Kind { get; set; }
    public int? Offset { get; set; }
}
