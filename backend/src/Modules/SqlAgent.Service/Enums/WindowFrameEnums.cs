namespace SqlAgent.Service.Enums;

public enum WindowFrameUnit
{
    Rows,
    Range
}

public enum WindowFrameBoundKind
{
    UnboundedPreceding,
    Preceding,
    CurrentRow,
    Following,
    UnboundedFollowing
}
