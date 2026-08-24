using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Normalization;

/// <summary>
/// Preserves the source-dialect semantics of DATEDIFF before the provider lowerers see the common
/// CORE_DATE_DIFF node. SQL Server and Firebird three-argument DATEDIFF count boundaries at the
/// requested date part, while MySQL's two-argument DATEDIFF ignores the time portion and returns a
/// day difference. Only DAY has a declared lossless cross-dialect intersection today.
/// </summary>
internal static class CoreDateDiffNormalizer
{
    public static SqlExpr Normalize(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) => sourceDialect switch
    {
        SqlAgentToolType.MySQL => NormalizeMySqlDateDiff(
            original,
            arguments,
            targetProvider),
        SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird => NormalizeBoundaryDateDiff(
            original,
            arguments,
            sourceDialect,
            targetProvider),
        _ => throw new SqlCompilationException(
            $"DATEDIFF is not a modeled source function for dialect {sourceDialect}.")
    };

    private static SqlExpr NormalizeMySqlDateDiff(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        SqlAgentToolType targetProvider)
    {
        if (arguments.Length != 2)
        {
            throw new SqlCompilationException(
                $"MySQL DATEDIFF requires exactly 2 arguments; received {arguments.Length}.");
        }

        // MySQL syntax is DATEDIFF(end, start), and only the date portions participate.
        return PortableDayDifference(
            original,
            start: arguments[1],
            end: arguments[0],
            targetProvider);
    }

    private static SqlExpr NormalizeBoundaryDateDiff(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider)
    {
        if (arguments.Length != 3)
        {
            throw new SqlCompilationException(
                $"{sourceDialect} DATEDIFF requires exactly 3 arguments; received {arguments.Length}.");
        }

        var unit = DatePartUnit(arguments[0]);
        if (sourceDialect == targetProvider)
        {
            // Preserve native SQL Server/Firebird behavior exactly, including their provider-specific
            // treatment of non-DAY boundaries and temporal data types.
            return Canonical(
                original,
                [new LiteralExpr(unit, original.Span), arguments[1], arguments[2]]);
        }

        if (unit != "DAY")
        {
            throw new SqlCompilationException(
                $"Cross-dialect DATEDIFF unit '{unit}' from {sourceDialect} to {targetProvider} is not translated: " +
                "date-part boundary rules are not declared equivalent for this provider pair. " +
                "DAY is the currently modeled lossless cross-dialect intersection.");
        }

        return PortableDayDifference(
            original,
            start: arguments[1],
            end: arguments[2],
            targetProvider);
    }

    private static SqlExpr PortableDayDifference(
        FunctionCallExpr original,
        SqlExpr start,
        SqlExpr end,
        SqlAgentToolType targetProvider)
    {
        var canonical = Canonical(
            original,
            [
                new LiteralExpr("DAY", original.Span),
                DateOnlyOperand(start, targetProvider, original.Span),
                DateOnlyOperand(end, targetProvider, original.Span)
            ]);

        // SQLite JULIANDAY subtraction is REAL-valued even when DATE() has reduced both operands to
        // midnight. The source DATEDIFF families return integral day counts, so make the result type
        // explicit instead of leaking a fractional/REAL semantic across the canonical boundary.
        return targetProvider == SqlAgentToolType.Sqlite
            ? new CastExpr(canonical, "INTEGER", original.Span)
            : canonical;
    }

    private static SqlExpr DateOnlyOperand(
        SqlExpr expression,
        SqlAgentToolType targetProvider,
        SourceSpan span) => targetProvider switch
    {
        // Oracle DATE retains a time-of-day, so CAST(... AS DATE) alone does not implement the
        // date-only semantics required here. TRUNC(CAST(... AS DATE)) removes the time portion.
        SqlAgentToolType.Oracle => new FunctionCallExpr(
            Identifier("TRUNC"),
            [new CastExpr(expression, "DATE", span)],
            IsDistinct: false,
            span),

        // MySQL DATE() and SQLite date() explicitly return the calendar-date portion.
        SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite => new FunctionCallExpr(
            Identifier("DATE"),
            [expression],
            IsDistinct: false,
            span),

        // PostgreSQL, SQL Server, and Firebird DATE casts drop the time-of-day for the target
        // expression shapes accepted by the Core pipeline.
        SqlAgentToolType.Postgres or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird =>
            new CastExpr(expression, "DATE", span),

        _ => throw new SqlCompilationException(
            $"Unsupported target provider '{targetProvider}' for portable DATEDIFF DAY normalization.")
    };

    private static FunctionCallExpr Canonical(
        FunctionCallExpr original,
        IEnumerable<SqlExpr> arguments) => original with
    {
        Name = Identifier("CORE_DATE_DIFF"),
        Arguments = arguments.ToImmutableArray()
    };

    private static string DatePartUnit(SqlExpr expression)
    {
        var unit = expression switch
        {
            BoundColumnExpr column => IdentifierText(column.Name),
            ColumnExpr column => IdentifierText(column.Name),
            LiteralExpr { Value: string value } => value,
            _ => throw new SqlCompilationException(
                "DATEDIFF date-part unit must be an unquoted SQL keyword.")
        };

        return unit.Trim().ToUpperInvariant() switch
        {
            "DAY" or "DD" or "D" => "DAY",
            "WEEK" or "WK" or "WW" => "WEEK",
            "MONTH" or "MM" or "M" => "MONTH",
            "QUARTER" or "QQ" or "Q" => "QUARTER",
            "YEAR" or "YY" or "YYYY" => "YEAR",
            "HOUR" or "HH" => "HOUR",
            "MINUTE" or "MI" or "N" => "MINUTE",
            "SECOND" or "SS" or "S" => "SECOND",
            _ => throw new SqlCompilationException(
                $"Unsupported DATEDIFF date-part unit '{unit}'.")
        };
    }

    private static SqlIdentifier Identifier(string name) =>
        new([new IdentifierPart(name, false, SourceSpan.Unknown)], SourceSpan.Unknown);

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
