using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Provider-specific temporal expression rendering for canonical date arithmetic, formatting,
/// parsing, and current temporal values. Shared shape/value helpers remain on the core expression
/// renderer so this module owns SQL spelling without duplicating semantic validation.
/// </summary>
internal static partial class NativeSqlExpressionRenderer
{
    private static NativeSqlFragment RenderDateAdd(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 3);
        var unit = LiteralKeyword(function.Arguments[0], "DATEADD unit");
        var capabilityError = SqlDateMathCapabilityRules.TargetValidationError(
            unit,
            provider,
            "CORE_DATE_ADD");
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

        var amount = Render(
            function.Arguments[1],
            provider,
            renderSubquery,
            dmlContext);
        var value = Render(
            function.Arguments[2],
            provider,
            renderSubquery,
            dmlContext);

        return provider switch
        {
            SqlAgentToolType.MsSqlServer => Combine(
                "DATEADD(" + unit + ", " + amount.Sql + ", " + value.Sql + ")",
                amount,
                value),
            SqlAgentToolType.MySQL => Combine(
                "TIMESTAMPADD(" + unit + ", " + amount.Sql + ", " + value.Sql + ")",
                amount,
                value),
            SqlAgentToolType.Postgres => Combine(
                "(" + value.Sql + " + (" + amount.Sql + " * INTERVAL '1 day'))",
                value,
                amount),
            SqlAgentToolType.Oracle => Combine(
                "(" + value.Sql + " + " + amount.Sql + ")",
                value,
                amount),
            SqlAgentToolType.Sqlite => Combine(
                "DATETIME(" + value.Sql + ", PRINTF('%+d day', " + amount.Sql + "))",
                value,
                amount),
            SqlAgentToolType.Firebird => Combine(
                "DATEADD(" + unit + ", " + amount.Sql + ", " + value.Sql + ")",
                amount,
                value),
            _ => throw new SqlCompilationException("Unsupported DATEADD provider.")
        };
    }

    private static NativeSqlFragment RenderDateDiff(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 3);
        var unit = LiteralKeyword(function.Arguments[0], "DATEDIFF unit");
        var capabilityError = SqlDateMathCapabilityRules.TargetValidationError(
            unit,
            provider,
            "CORE_DATE_DIFF");
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

        var start = Render(
            function.Arguments[1],
            provider,
            renderSubquery,
            dmlContext);
        var end = Render(
            function.Arguments[2],
            provider,
            renderSubquery,
            dmlContext);

        return provider switch
        {
            SqlAgentToolType.MsSqlServer => Combine(
                "DATEDIFF(" + unit + ", " + start.Sql + ", " + end.Sql + ")",
                start,
                end),
            SqlAgentToolType.MySQL => Combine(
                "TIMESTAMPDIFF(" + unit + ", " + start.Sql + ", " + end.Sql + ")",
                start,
                end),
            SqlAgentToolType.Postgres => Combine(
                "(CAST(" + end.Sql + " AS date) - CAST(" + start.Sql + " AS date))",
                end,
                start),
            SqlAgentToolType.Oracle => Combine(
                "(CAST(" + end.Sql + " AS DATE) - CAST(" + start.Sql + " AS DATE))",
                end,
                start),
            SqlAgentToolType.Sqlite => Combine(
                "(JULIANDAY(" + end.Sql + ") - JULIANDAY(" + start.Sql + "))",
                end,
                start),
            SqlAgentToolType.Firebird => Combine(
                "DATEDIFF(" + unit + " FROM " + start.Sql + " TO " + end.Sql + ")",
                start,
                end),
            _ => throw new SqlCompilationException("Unsupported DATEDIFF provider.")
        };
    }

    private static NativeSqlFragment RenderDatePart(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 2);
        var part = LiteralKeyword(function.Arguments[0], "date part");
        var capabilityError = SqlDatePartCapabilityRules.TargetValidationError(part, provider);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

        var value = Render(
            function.Arguments[1],
            provider,
            renderSubquery,
            dmlContext);

        var sql = provider switch
        {
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.MySQL =>
                part + "(" + value.Sql + ")",
            SqlAgentToolType.Postgres or SqlAgentToolType.Oracle =>
                "EXTRACT(" + part + " FROM " + value.Sql + ")",
            SqlAgentToolType.Firebird =>
                "EXTRACT(" + part + " FROM CAST(" + value.Sql + " AS DATE))",
            SqlAgentToolType.Sqlite => part switch
            {
                "YEAR" => "CAST(STRFTIME('%Y', " + value.Sql + ") AS INTEGER)",
                "MONTH" => "CAST(STRFTIME('%m', " + value.Sql + ") AS INTEGER)",
                "DAY" => "CAST(STRFTIME('%d', " + value.Sql + ") AS INTEGER)",
                _ => throw new SqlCompilationException(
                    "SQLite does not support date part " + part + ".")
            },
            _ => throw new SqlCompilationException("Unsupported date-part provider.")
        };

        return value with { Sql = sql };
    }

    private static NativeSqlFragment RenderDateFormat(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 2);
        var capabilityError = SqlTemporalFormatCapabilityRules.TargetValidationError(
            "CORE_DATE_FORMAT",
            provider);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery,
            dmlContext);
        var formatValue = StringLiteralValue(
            function.Arguments[1],
            "date format");
        var format = BindShared(
            "date-format:" + formatValue,
            formatValue);

        return provider switch
        {
            SqlAgentToolType.MsSqlServer => Combine(
                "FORMAT(" + value.Sql + ", " + format.Sql + ")",
                value,
                format),
            SqlAgentToolType.Postgres or SqlAgentToolType.Oracle => Combine(
                "TO_CHAR(" + value.Sql + ", " + format.Sql + ")",
                value,
                format),
            SqlAgentToolType.MySQL => Combine(
                "DATE_FORMAT(" + value.Sql + ", " + format.Sql + ")",
                value,
                format),
            SqlAgentToolType.Sqlite => Combine(
                "STRFTIME(" + format.Sql + ", " + value.Sql + ")",
                format,
                value),
            SqlAgentToolType.Firebird => throw new SqlCompilationException(
                "portable date formatting is not supported by Firebird."),
            _ => throw new SqlCompilationException("Unsupported date-format provider.")
        };
    }

    private static NativeSqlFragment RenderDateParse(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 2);
        var capabilityError = SqlTemporalFormatCapabilityRules.TargetValidationError(
            "CORE_DATE_PARSE",
            provider);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery,
            dmlContext);
        var formatValue = StringLiteralValue(
            function.Arguments[1],
            "date parse format");
        var format = BindShared(
            "date-parse-format:" + formatValue,
            formatValue);

        return provider switch
        {
            SqlAgentToolType.MySQL => Combine(
                "DATE(STR_TO_DATE(" + value.Sql + ", " + format.Sql + "))",
                value,
                format),
            SqlAgentToolType.Postgres or SqlAgentToolType.Oracle => Combine(
                "TO_DATE(" + value.Sql + ", " + format.Sql + ")",
                value,
                format),
            _ => throw new SqlCompilationException(
                "formatted date parsing is not supported by this provider.")
        };
    }

    private static NativeSqlFragment RenderCurrentDate(
        FunctionCallExpr function,
        SqlAgentToolType provider)
    {
        RequireCurrentTemporalShape(function);
        return new NativeSqlFragment(
            provider == SqlAgentToolType.MsSqlServer
                ? "CAST(CURRENT_TIMESTAMP AS date)"
                : "CURRENT_DATE",
            ImmutableArray<object?>.Empty);
    }

    private static NativeSqlFragment RenderCurrentTime(
        FunctionCallExpr function,
        SqlAgentToolType provider)
    {
        RequireCurrentTemporalShape(function);
        if (provider == SqlAgentToolType.Oracle)
            throw new SqlCompilationException("CURRENT_TIME is not supported by Oracle.");

        return new NativeSqlFragment(
            provider == SqlAgentToolType.MsSqlServer
                ? "CAST(CURRENT_TIMESTAMP AS time)"
                : "CURRENT_TIME",
            ImmutableArray<object?>.Empty);
    }

    private static NativeSqlFragment RenderCurrentTimestamp(
        FunctionCallExpr function)
    {
        RequireCurrentTemporalShape(function);
        return new NativeSqlFragment(
            "CURRENT_TIMESTAMP",
            ImmutableArray<object?>.Empty);
    }
}
