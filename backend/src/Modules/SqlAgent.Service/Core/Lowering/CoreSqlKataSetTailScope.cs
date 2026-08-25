using SqlAgent.Service.Core.Ast;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Defines the portable direct-tail subset for a set-operation query. Core normally places a set
/// query behind SELECT * FROM (...) _set so SqlKata can render ORDER/LIMIT after UNION. That wrapper
/// is not correlation-safe inside scalar/EXISTS expressions, so only output-name and output-ordinal
/// ordering may use the direct set-tail renderer. Richer ordering remains fail-closed there.
/// </summary>
internal static class CoreSqlKataSetTailScope
{
    public static bool CanRenderDirectTail(QueryStatement statement) =>
        !statement.SetOperations.IsDefaultOrEmpty
        && !statement.Head.Ctes.IsDefaultOrEmpty
        && RequiresTail(statement)
        && statement.OrderBy.All(item => IsPortableSetOutputReference(item.Expression));

    private static bool RequiresTail(QueryStatement statement) =>
        !statement.OrderBy.IsDefaultOrEmpty
        || statement.Limit is not null
        || statement.Offset is > 0;

    private static bool IsPortableSetOutputReference(SqlExpr expression) => expression switch
    {
        LiteralExpr { Value: OrderByOrdinalValue } => true,
        ColumnExpr column => IsSingleOutputName(column.Name),
        BoundColumnExpr column => IsSingleOutputName(column.Name),
        _ => false
    };

    private static bool IsSingleOutputName(SqlIdentifier identifier) =>
        identifier.Parts.Length == 1
        && (identifier.Parts[0].WasQuoted || identifier.Parts[0].Value != "*");
}
