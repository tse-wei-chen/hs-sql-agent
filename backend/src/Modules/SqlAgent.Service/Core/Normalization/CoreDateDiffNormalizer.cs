using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Normalization;

/// <summary>
/// Preserves native source-dialect DATEDIFF semantics when the parsed shape is native, while also
/// accepting the three-argument DATEDIFF(unit, start, end) shape as the Core portable contract used
/// by structured queries. SQL Server and Firebird three-argument DATEDIFF count boundaries at the
/// requested date part, while MySQL's native two-argument DATEDIFF ignores the time portion and
/// returns a day difference. Only DAY has a declared lossless cross-dialect intersection today.
/// </summary>
internal static class CoreDateDiffNormalizer
{
    public static SqlExpr Normalize(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider)
    {
        if (arguments.Length == 3)
        {
            return NormalizeThreeArgumentDateDiff(
                original,
                arguments,
                sourceDialect,
                targetProvider);
        }

        if (sourceDialect == SqlAgentToolType.MySQL)
        {
            return NormalizeMySqlDateDiff(
                original,
                arguments,
                targetProvider);
        }

        throw new SqlCompilationException(
            $"DATEDIFF is not a modeled {arguments.Length}-argument source function for dialect {sourceDialect}. " +
            "Use the Core portable DATEDIFF(unit, start, end) shape.");
    }

    private static SqlExpr NormalizeThreeArgumentDateDiff(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider)
    {
        var unit = DatePartUnit(arguments[0]);

        if (sourceDialect is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird
            && sourceDialect == targetProvider)
        {
            // Preserve native SQL Server/Firebird behavior exactly when the three-argument shape is
            // executed by its native provider, including provider-specific non-DAY boundary rules.
            return Canonical(
                original,
                [new LiteralExpr(unit, original.Span), arguments[1], arguments[2]]);
        }

        if (unit != "DAY")
        {
            var capability = $"core_date_diff.unit.{unit.ToLowerInvariant()}";
            throw new SqlCompilationException(
                $"SQL capability '{capability}' is not modeled losslessly for DATEDIFF from " +
                $"{sourceDialect} to {targetProvider}. DAY is the currently modeled portable intersection.");
        }

        return PortableDayDifference(
            original,
            start: arguments[1],
            end: arguments[2],
            targetProvider);
    }

    private static SqlExpr NormalizeMySqlDateDiff(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        SqlAgentToolType targetProvider)
    {
        if (arguments.Length != 2)
        {
            throw new SqlCompilationException(
                $"MySQL native DATEDIFF requires exactly 2 arguments; received {arguments.Length}. " +
                "Use DATEDIFF(unit, start, end) for the Core portable form.");
        }

        // MySQL syntax is DATEDIFF(end, start), and only the date portions participate.
        return PortableDayDifference(
            original,
            start: arguments[1],
            end: arguments[0],
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
