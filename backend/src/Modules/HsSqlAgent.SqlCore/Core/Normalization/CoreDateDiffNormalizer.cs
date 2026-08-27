using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Normalization;

/// <summary>
/// Preserves native source-dialect DATEDIFF semantics when the parsed shape is native, while also
/// accepting the portable DATEDIFF shapes used by structured queries. The two-argument portable
/// shape is DATEDIFF(end, start), matching MySQL native DATEDIFF and returning an integral calendar
/// day difference. The three-argument portable shape is DATEDIFF(unit, start, end). SQL Server and
/// Firebird native three-argument DATEDIFF count boundaries at the requested date part. Only DAY has
/// a declared lossless cross-dialect intersection today.
/// </summary>
internal static class CoreDateDiffNormalizer
{
    public static SqlExpr Normalize(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) => arguments.Length switch
    {
        2 => NormalizeTwoArgumentDateDiff(original, arguments, targetProvider),
        3 => NormalizeThreeArgumentDateDiff(original, arguments, sourceDialect, targetProvider),
        _ => throw new SqlCompilationException(
            $"DATEDIFF requires either the portable 2-argument (end, start) shape or the " +
            $"3-argument (unit, start, end) shape; received {arguments.Length} arguments.")
    };

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
                $"Cross-dialect DATEDIFF unit '{unit}' from {sourceDialect} to {targetProvider} is not translated: " +
                $"SQL capability '{capability}' is not modeled losslessly. " +
                "DAY is the currently modeled portable intersection.");
        }

        return PortableDayDifference(
            original,
            start: arguments[1],
            end: arguments[2],
            targetProvider);
    }

    private static SqlExpr NormalizeTwoArgumentDateDiff(
        FunctionCallExpr original,
        ImmutableArray<SqlExpr> arguments,
        SqlAgentToolType targetProvider)
    {
        // Structured Core queries historically expose DATEDIFF(end, start) as a portable day
        // difference. This is also exactly MySQL's native DATEDIFF argument order and date-only
        // semantics. Parser-native SQL from other dialects is rejected earlier by
        // CoreSourceDialectValidator when source-syntax enforcement is enabled.
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

        return SqlDateMathCapabilityRules.NormalizeUnit(
            unit,
            "DATEDIFF");
    }

    private static SqlIdentifier Identifier(string name) =>
        new([new IdentifierPart(name, false, SourceSpan.Unknown)], SourceSpan.Unknown);

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
