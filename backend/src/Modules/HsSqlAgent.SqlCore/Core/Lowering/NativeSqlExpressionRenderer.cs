using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Renders canonical expressions directly from Core AST. Every runtime value is represented by an
/// internal positional marker and ordered binding; target parameter names are assigned only after
/// the complete statement has been rendered.
/// </summary>
internal static class NativeSqlExpressionRenderer
{
    private static readonly Regex SafeCastType = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*(?:\((?:MAX|[0-9]+(?:,[0-9]+)?)\))?(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string P => NativeSqlParameterizer.Placeholder;

    public static NativeSqlFragment Render(
        SqlExpr expression,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext = false)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(renderSubquery);

        return expression switch
        {
            BoundColumnExpr column => RenderIdentifier(column.Name, provider),
            ColumnExpr column => RenderIdentifier(column.Name, provider),
            LiteralExpr literal => RenderLiteral(literal, provider),
            IntervalExpr interval => RenderInterval(interval, provider),
            UnaryExpr unary => RenderUnary(unary, provider, renderSubquery, dmlContext),
            BinaryExpr binary => RenderBinary(binary, provider, renderSubquery, dmlContext),
            FunctionCallExpr function => RenderFunction(
                function,
                provider,
                renderSubquery,
                dmlContext),
            FilterExpr filter when dmlContext => throw new SqlCompilationException(
                "FILTER expressions are not supported in Core DML rendering."),
            FilterExpr filter => RenderFilter(filter, provider, renderSubquery),
            WindowedExpr when dmlContext => throw new SqlCompilationException(
                "Window expressions are not supported in Core DML rendering."),
            WindowedExpr windowed => RenderWindowed(windowed, provider, renderSubquery),
            CastExpr cast => RenderCast(cast, provider, renderSubquery, dmlContext),
            SimpleCaseExpr simpleCase => RenderSimpleCase(
                simpleCase,
                provider,
                renderSubquery,
                dmlContext),
            CaseExpr @case => RenderCase(@case, provider, renderSubquery, dmlContext),
            InExpr @in => RenderIn(@in, provider, renderSubquery, dmlContext),
            BetweenExpr between => RenderBetween(
                between,
                provider,
                renderSubquery,
                dmlContext),
            IsNullExpr isNull => RenderIsNull(
                isNull,
                provider,
                renderSubquery,
                dmlContext),
            SubqueryExpr subquery => RenderSubquery(subquery.Query, renderSubquery),
            ExistsExpr exists => RenderExists(exists, renderSubquery),
            _ => throw new SqlCompilationException(
                "Unsupported expression during native lowering: " +
                expression.GetType().Name)
        };
    }

    private static NativeSqlFragment RenderLiteral(
        LiteralExpr literal,
        SqlAgentToolType provider)
    {
        if (literal.Value is SqlTimeValue && provider == SqlAgentToolType.Oracle)
            throw new SqlCompilationException("Oracle has no standalone TIME data type.");

        if (literal.Value is SqlOffsetDateTimeValue
            && provider == SqlAgentToolType.MySQL)
        {
            throw new SqlCompilationException(
                "MySQL has no native timestamp type that preserves a UTC offset.");
        }

        if (provider == SqlAgentToolType.Postgres)
        {
            if (literal.Value is SqlOffsetDateTimeValue offsetValue)
            {
                return Bind(offsetValue.Value.ToUniversalTime());
            }

            if (literal.Value is DateTimeOffset rawOffset)
                return Bind(rawOffset.ToUniversalTime());
        }

        var value = NativeSqlValueNormalizer.Normalize(literal.Value);
        if (provider != SqlAgentToolType.Firebird)
            return Bind(value);

        if (literal.Value is SqlDateValue)
            return CastBinding("DATE", value);
        if (literal.Value is SqlTimeValue)
            return CastBinding("TIME", value);
        if (literal.Value is SqlLocalDateTimeValue)
            return CastBinding("TIMESTAMP", value);
        if (literal.Value is SqlOffsetDateTimeValue offset)
        {
            return CastBinding(
                "TIMESTAMP WITH TIME ZONE",
                FormatFirebirdOffsetTimestamp(offset.Value));
        }

        return value switch
        {
            DateOnly => CastBinding("DATE", value),
            TimeOnly or TimeSpan => CastBinding("TIME", value),
            DateTime => CastBinding("TIMESTAMP", value),
            DateTimeOffset dateTimeOffset => CastBinding(
                "TIMESTAMP WITH TIME ZONE",
                FormatFirebirdOffsetTimestamp(dateTimeOffset)),
            string text => RenderFirebirdString(text),
            bool => CastBinding("BOOLEAN", value),
            byte or sbyte or short or ushort or int => CastBinding("INTEGER", value),
            uint or long => CastBinding("BIGINT", value),
            decimal => CastBinding("DECIMAL(38,10)", value),
            double or float => CastBinding("DOUBLE PRECISION", value),
            _ => Bind(value)
        };
    }

    private static NativeSqlFragment Bind(object? value) =>
        new(P, [value]);

    private static NativeSqlFragment BindShared(
        string key,
        object? value) =>
        new(P, [new NativeSharedSqlBinding(key, value)]);

    private static NativeSqlFragment CastBinding(string type, object? value) =>
        new("CAST(" + P + " AS " + type + ")", [value]);

    private static NativeSqlFragment RenderFirebirdString(string value)
    {
        const int maxFirebirdUtf8VarcharChars = 8191;
        if (value.Length > maxFirebirdUtf8VarcharChars)
        {
            throw new SqlCompilationException(
                "Firebird string literal exceeds the safe UTF8 VARCHAR limit of " +
                maxFirebirdUtf8VarcharChars + " characters.");
        }

        var length = Math.Max(1, value.Length);
        return CastBinding("VARCHAR(" + length + ")", value);
    }

    private static string FormatFirebirdOffsetTimestamp(DateTimeOffset value) =>
        value.ToString(
            "yyyy-MM-dd HH:mm:ss.fffffff zzz",
            CultureInfo.InvariantCulture);

    private static NativeSqlFragment RenderInterval(
        IntervalExpr interval,
        SqlAgentToolType provider)
    {
        if (provider != SqlAgentToolType.Postgres)
        {
            throw new SqlCompilationException(
                "INTERVAL expressions are supported only by PostgreSQL in the Core backend.");
        }

        return new NativeSqlFragment(
            "CAST(" + P + " AS interval)",
            [interval.Literal]);
    }

    private static NativeSqlFragment RenderUnary(
        UnaryExpr unary,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        if (unary.Operator != "NOT")
        {
            throw new SqlCompilationException(
                "Unsupported unary operator '" + unary.Operator + "'.");
        }

        var operand = Render(unary.Operand, provider, renderSubquery, dmlContext);
        return operand with { Sql = "NOT (" + operand.Sql + ")" };
    }

    private static NativeSqlFragment RenderBinary(
        BinaryExpr binary,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        var left = Render(binary.Left, provider, renderSubquery, dmlContext);
        var right = Render(binary.Right, provider, renderSubquery, dmlContext);
        var likeEscape = CoreLikeEscapeSqlRenderer.RenderSuffix(binary, provider);

        if (binary.Operator == "%"
            && SqlModuloCapabilityRules.UsesFunctionSyntax(provider))
        {
            return Combine(
                "MOD(" + left.Sql + ", " + right.Sql + ")",
                left,
                right);
        }

        if (binary.Operator == "||"
            && SqlConcatCapabilityRules.UsesConcatFunctionForCanonicalPipes(provider))
        {
            return Combine(
                "CONCAT(" + left.Sql + ", " + right.Sql + ")",
                left,
                right);
        }

        var op = binary.Operator switch
        {
            "+" or "-" or "*" or "/" or "%" or "||" or
            "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=" or
            "LIKE" or "ILIKE" or "AND" or "OR" or "IN" or "NOT IN" =>
                binary.Operator,
            _ => throw new SqlCompilationException(
                "Unsupported binary operator '" + binary.Operator + "'.")
        };

        return Combine(
            "(" + left.Sql + " " + op + " " + right.Sql + likeEscape + ")",
            left,
            right);
    }

    private static NativeSqlFragment RenderFunction(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        if (dmlContext && name == "CORE_STRING_AGG")
        {
            throw new SqlCompilationException(
                "Aggregate function CORE_STRING_AGG is not valid in a DML expression.");
        }

        return name switch
        {
            "CORE_DATE_ADD" => RenderDateAdd(
                function,
                provider,
                renderSubquery,
                dmlContext),
            "CORE_DATE_DIFF" => RenderDateDiff(
                function,
                provider,
                renderSubquery,
                dmlContext),
            "CORE_DATE_PART" => RenderDatePart(
                function,
                provider,
                renderSubquery,
                dmlContext),
            "CORE_DATE_FORMAT" => RenderDateFormat(
                function,
                provider,
                renderSubquery,
                dmlContext),
            "CORE_DATE_PARSE" => RenderDateParse(
                function,
                provider,
                renderSubquery,
                dmlContext),
            "CORE_POSITION" => RenderPosition(
                function,
                provider,
                renderSubquery,
                dmlContext),
            "CORE_JSON_EXTRACT" => RenderJsonExtract(
                function,
                provider,
                renderSubquery,
                dmlContext),
            "CORE_JSON_SET" => RenderJsonSet(
                function,
                provider,
                renderSubquery,
                dmlContext),
            "CORE_REGEX_MATCH" => RenderRegexMatch(
                function,
                provider,
                renderSubquery,
                dmlContext),
            "CORE_CURRENT_DATE" => RenderCurrentDate(function, provider),
            "CORE_CURRENT_TIME" => RenderCurrentTime(function, provider),
            "CORE_CURRENT_TIMESTAMP" => RenderCurrentTimestamp(function),
            "CORE_STRING_AGG" => RenderStringAggregate(
                function,
                provider,
                renderSubquery),
            _ => RenderOrdinaryFunction(
                function,
                provider,
                renderSubquery,
                dmlContext)
        };
    }

    private static NativeSqlFragment RenderOrdinaryFunction(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        var name = IdentifierText(function.Name);
        if (!Regex.IsMatch(
                name,
                @"^[A-Za-z_][A-Za-z0-9_]*$",
                RegexOptions.CultureInvariant))
        {
            throw new SqlCompilationException(
                "Unsafe function identifier '" + name + "'.");
        }

        if (name.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlCompilationException(
                "Canonical function '" + name +
                "' has no native lowering implementation; compilation was rejected.");
        }

        var args = function.Arguments
            .Select(argument =>
                Render(argument, provider, renderSubquery, dmlContext))
            .ToArray();
        var renderedArgs = args.Select(argument => argument.Sql).ToArray();

        if (provider == SqlAgentToolType.Postgres
            && name.Equals("ROUND", StringComparison.OrdinalIgnoreCase)
            && args.Length == 2)
        {
            renderedArgs[0] = "CAST(" + renderedArgs[0] + " AS numeric)";
        }

        var argumentSql = string.Join(", ", renderedArgs);
        if (function.IsDistinct)
            argumentSql = "DISTINCT " + argumentSql;

        return new NativeSqlFragment(
            name + "(" + argumentSql + ")",
            args.SelectMany(argument => argument.Bindings).ToImmutableArray());
    }

    private static NativeSqlFragment RenderDateAdd(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 3);
        var unit = LiteralKeyword(function.Arguments[0], "DATEADD unit");
        if (unit != "DAY"
            && provider is SqlAgentToolType.Postgres
                or SqlAgentToolType.Oracle
                or SqlAgentToolType.Sqlite)
        {
            throw new SqlCompilationException(
                "DATEADD unit " + unit + " is not supported by " + provider + ".");
        }

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
        if (unit != "DAY"
            && provider is SqlAgentToolType.Postgres
                or SqlAgentToolType.Oracle
                or SqlAgentToolType.Sqlite)
        {
            throw new SqlCompilationException(
                "DATEDIFF unit " + unit + " is not supported by " + provider + ".");
        }

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

    private static NativeSqlFragment RenderPosition(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 2);
        var haystack = Render(
            function.Arguments[0],
            provider,
            renderSubquery,
            dmlContext);
        var needle = Render(
            function.Arguments[1],
            provider,
            renderSubquery,
            dmlContext);

        return provider switch
        {
            SqlAgentToolType.MsSqlServer => Combine(
                "CHARINDEX(" + needle.Sql + ", " + haystack.Sql + ")",
                needle,
                haystack),
            SqlAgentToolType.Postgres => Combine(
                "STRPOS(" + haystack.Sql + ", " + needle.Sql + ")",
                haystack,
                needle),
            SqlAgentToolType.MySQL => Combine(
                "LOCATE(" + needle.Sql + ", " + haystack.Sql + ")",
                needle,
                haystack),
            SqlAgentToolType.Sqlite or SqlAgentToolType.Oracle => Combine(
                "INSTR(" + haystack.Sql + ", " + needle.Sql + ")",
                haystack,
                needle),
            SqlAgentToolType.Firebird => Combine(
                "POSITION(" + needle.Sql + ", " + haystack.Sql + ")",
                needle,
                haystack),
            _ => throw new SqlCompilationException("Unsupported position provider.")
        };
    }

    private static NativeSqlFragment RenderJsonExtract(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 2);
        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery,
            dmlContext);

        if (provider is SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite)
        {
            var path = Render(
                function.Arguments[1],
                provider,
                renderSubquery,
                dmlContext);
            return Combine(
                "JSON_EXTRACT(" + value.Sql + ", " + path.Sql + ")",
                value,
                path);
        }

        if (provider != SqlAgentToolType.Postgres)
        {
            throw new SqlCompilationException(
                "JSON_EXTRACT is not supported losslessly by this provider.");
        }

        var segments = JsonPathSegments(function.Arguments[1]);
        var bindings = value.Bindings.ToBuilder();
        var placeholders = new List<string>();
        foreach (var segment in segments)
        {
            placeholders.Add(P);
            bindings.Add(segment);
        }

        return new NativeSqlFragment(
            "JSONB_EXTRACT_PATH(CAST(" + value.Sql + " AS jsonb), " +
            string.Join(", ", placeholders) + ")",
            bindings.ToImmutable());
    }

    private static NativeSqlFragment RenderJsonSet(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 3);
        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery,
            dmlContext);
        var newValue = Render(
            function.Arguments[2],
            provider,
            renderSubquery,
            dmlContext);

        if (provider is SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite)
        {
            var path = Render(
                function.Arguments[1],
                provider,
                renderSubquery,
                dmlContext);
            return new NativeSqlFragment(
                "JSON_SET(" + value.Sql + ", " + path.Sql + ", " + newValue.Sql + ")",
                value.Bindings
                    .Concat(path.Bindings)
                    .Concat(newValue.Bindings)
                    .ToImmutableArray());
        }

        if (provider == SqlAgentToolType.MsSqlServer)
        {
            var path = Render(
                function.Arguments[1],
                provider,
                renderSubquery,
                dmlContext);
            return new NativeSqlFragment(
                "JSON_MODIFY(" + value.Sql + ", " + path.Sql + ", " + newValue.Sql + ")",
                value.Bindings
                    .Concat(path.Bindings)
                    .Concat(newValue.Bindings)
                    .ToImmutableArray());
        }

        if (provider != SqlAgentToolType.Postgres)
        {
            throw new SqlCompilationException(
                "JSON_SET is not supported by this provider.");
        }

        var pgPath = "{" +
            string.Join(',', JsonPathSegments(function.Arguments[1])) +
            "}";
        return new NativeSqlFragment(
            "JSONB_SET(CAST(" + value.Sql + " AS jsonb), CAST(" + P +
            " AS text[]), TO_JSONB(" + newValue.Sql + "))",
            value.Bindings
                .Concat([pgPath])
                .Concat(newValue.Bindings)
                .ToImmutableArray());
    }

    private static NativeSqlFragment RenderRegexMatch(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        RequireArguments(function, 2);
        if (provider is SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Sqlite
            or SqlAgentToolType.Firebird)
        {
            throw new SqlCompilationException(
                "REGEXP_LIKE is not supported by this provider.");
        }

        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery,
            dmlContext);
        var pattern = Render(
            function.Arguments[1],
            provider,
            renderSubquery,
            dmlContext);

        return provider == SqlAgentToolType.Postgres
            ? Combine(
                "(" + value.Sql + " ~ " + pattern.Sql + ")",
                value,
                pattern)
            : Combine(
                "REGEXP_LIKE(" + value.Sql + ", " + pattern.Sql + ")",
                value,
                pattern);
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

    private static NativeSqlFragment RenderStringAggregate(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery)
    {
        RequireArguments(function, 2);
        if (function.IsDistinct)
        {
            throw new SqlCompilationException(
                "Canonical CORE_STRING_AGG DISTINCT semantics are not enabled.");
        }

        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery);
        var separator = provider == SqlAgentToolType.Postgres
            ? Bind(StringLiteralValue(
                function.Arguments[1],
                "string aggregate separator"))
            : new NativeSqlFragment(
                SqlStringLiteral(
                    function.Arguments[1],
                    "string aggregate separator",
                    provider),
                ImmutableArray<object?>.Empty);

        if (!function.AggregateOrderBy.IsDefaultOrEmpty)
        {
            var ordering = RenderOrderByClause(
                function.AggregateOrderBy,
                provider,
                renderSubquery,
                "aggregate");
            var orderedSql = provider switch
            {
                SqlAgentToolType.Postgres =>
                    "STRING_AGG(" + value.Sql + ", " + separator.Sql + " " +
                    ordering.Sql + ")",
                SqlAgentToolType.Sqlite =>
                    "GROUP_CONCAT(" + value.Sql + ", " + separator.Sql + " " +
                    ordering.Sql + ")",
                SqlAgentToolType.MsSqlServer =>
                    "STRING_AGG(" + value.Sql + ", " + separator.Sql +
                    ") WITHIN GROUP (" + ordering.Sql + ")",
                SqlAgentToolType.Oracle =>
                    "LISTAGG(" + value.Sql + ", " + separator.Sql +
                    ") WITHIN GROUP (" + ordering.Sql + ")",
                SqlAgentToolType.MySQL =>
                    "GROUP_CONCAT(" + value.Sql + " " + ordering.Sql +
                    " SEPARATOR " + separator.Sql + ")",
                _ => throw new SqlCompilationException(
                    "Aggregate-local ORDER BY lowering is not supported by " +
                    provider + ".")
            };

            return new NativeSqlFragment(
                orderedSql,
                value.Bindings
                    .Concat(separator.Bindings)
                    .Concat(ordering.Bindings)
                    .ToImmutableArray());
        }

        var sql = provider switch
        {
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.Postgres =>
                "STRING_AGG(" + value.Sql + ", " + separator.Sql + ")",
            SqlAgentToolType.MySQL =>
                "GROUP_CONCAT(" + value.Sql + " SEPARATOR " + separator.Sql + ")",
            SqlAgentToolType.Sqlite =>
                "GROUP_CONCAT(" + value.Sql + ", " + separator.Sql + ")",
            SqlAgentToolType.Oracle =>
                "LISTAGG(" + value.Sql + ", " + separator.Sql + ")",
            SqlAgentToolType.Firebird =>
                "LIST(" + value.Sql + ", " + separator.Sql + ")",
            _ => throw new SqlCompilationException(
                "Unsupported string aggregate provider.")
        };

        return new NativeSqlFragment(
            sql,
            value.Bindings
                .Concat(separator.Bindings)
                .ToImmutableArray());
    }

    private static NativeSqlFragment RenderFilter(
        FilterExpr filter,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery)
    {
        if (provider is not (
            SqlAgentToolType.Postgres or
            SqlAgentToolType.Sqlite or
            SqlAgentToolType.Oracle or
            SqlAgentToolType.Firebird))
        {
            throw new SqlCompilationException(
                "FILTER lowering is not supported by " + provider + ".");
        }

        var expression = Render(
            filter.Expression,
            provider,
            renderSubquery);
        var predicate = Render(
            filter.Predicate,
            provider,
            renderSubquery);
        return new NativeSqlFragment(
            expression.Sql + " FILTER (WHERE " + predicate.Sql + ")",
            expression.Bindings
                .Concat(predicate.Bindings)
                .ToImmutableArray());
    }

    private static NativeSqlFragment RenderOrderByClause(
        ImmutableArray<OrderByItem> orderBy,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        string context)
    {
        var orderParts = new List<string>();
        var bindings = ImmutableArray.CreateBuilder<object?>();

        foreach (var item in orderBy)
        {
            var rendered = Render(
                item.Expression,
                provider,
                renderSubquery);
            var sql = rendered.Sql + (item.Descending ? " DESC" : " ASC");
            sql += item.NullOrdering switch
            {
                NullOrderingKind.Default => string.Empty,
                NullOrderingKind.First => " NULLS FIRST",
                NullOrderingKind.Last => " NULLS LAST",
                _ => throw new SqlCompilationException(
                    "Unsupported NULL ordering '" + item.NullOrdering +
                    "' in " + context + ".")
            };

            orderParts.Add(sql);
            bindings.AddRange(rendered.Bindings);
        }

        return new NativeSqlFragment(
            "ORDER BY " + string.Join(", ", orderParts),
            bindings.ToImmutable());
    }

    private static NativeSqlFragment RenderWindowed(
        WindowedExpr windowed,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery)
    {
        var expression = Render(
            windowed.Expression,
            provider,
            renderSubquery);
        var parts = new List<string>();
        var bindings = ImmutableArray.CreateBuilder<object?>();
        bindings.AddRange(expression.Bindings);

        if (!windowed.Window.PartitionBy.IsDefaultOrEmpty)
        {
            var partition = windowed.Window.PartitionBy
                .Select(item => Render(item, provider, renderSubquery))
                .ToArray();
            parts.Add(
                "PARTITION BY " +
                string.Join(", ", partition.Select(item => item.Sql)));
            foreach (var item in partition)
                bindings.AddRange(item.Bindings);
        }

        if (!windowed.Window.OrderBy.IsDefaultOrEmpty)
        {
            var ordering = RenderOrderByClause(
                windowed.Window.OrderBy,
                provider,
                renderSubquery,
                "window");
            parts.Add(ordering.Sql);
            bindings.AddRange(ordering.Bindings);
        }

        if (windowed.Window.Frame is not null)
            parts.Add(RenderWindowFrame(windowed.Window.Frame));

        return new NativeSqlFragment(
            expression.Sql + " OVER (" + string.Join(" ", parts) + ")",
            bindings.ToImmutable());
    }

    private static string RenderWindowFrame(WindowFrame frame)
    {
        var unit = frame.Unit switch
        {
            WindowFrameUnitKind.Rows => "ROWS",
            WindowFrameUnitKind.Range => "RANGE",
            _ => throw new SqlCompilationException(
                "Unsupported window frame unit '" + frame.Unit + "'.")
        };

        var start = RenderWindowBound(frame.Start);
        return frame.End is null
            ? unit + " " + start
            : unit + " BETWEEN " + start + " AND " +
              RenderWindowBound(frame.End);
    }

    private static string RenderWindowBound(
        WindowFrameBoundCore bound) => bound.Kind switch
    {
        WindowFrameBoundKindCore.UnboundedPreceding =>
            "UNBOUNDED PRECEDING",
        WindowFrameBoundKindCore.Preceding when bound.Offset is >= 0 =>
            bound.Offset.Value + " PRECEDING",
        WindowFrameBoundKindCore.CurrentRow =>
            "CURRENT ROW",
        WindowFrameBoundKindCore.Following when bound.Offset is >= 0 =>
            bound.Offset.Value + " FOLLOWING",
        WindowFrameBoundKindCore.UnboundedFollowing =>
            "UNBOUNDED FOLLOWING",
        _ => throw new SqlCompilationException(
            "Invalid window frame bound '" + bound.Kind + "'.")
    };

    private static NativeSqlFragment RenderCast(
        CastExpr cast,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        if (!SafeCastType.IsMatch(cast.TypeName))
        {
            throw new SqlCompilationException(
                "Unsafe CAST type '" + cast.TypeName + "'.");
        }

        var inner = Render(
            cast.Expression,
            provider,
            renderSubquery,
            dmlContext);
        return inner with
        {
            Sql = "CAST(" + inner.Sql + " AS " + cast.TypeName + ")"
        };
    }

    private static NativeSqlFragment RenderSimpleCase(
        SimpleCaseExpr @case,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        if (@case.Branches.IsDefaultOrEmpty)
            throw new SqlCompilationException("Simple CASE requires at least one WHEN branch.");

        var first = RequireSimpleCaseComparison(@case.Branches[0]);
        var operand = Render(
            first.Left,
            provider,
            renderSubquery,
            dmlContext);
        var bindings = ImmutableArray.CreateBuilder<object?>();
        bindings.AddRange(operand.Bindings);
        var parts = new List<string>();

        foreach (var branch in @case.Branches)
        {
            var comparison = RequireSimpleCaseComparison(branch);
            var branchOperand = Render(
                comparison.Left,
                provider,
                renderSubquery,
                dmlContext);
            if (!EquivalentFragment(operand, branchOperand))
            {
                throw new SqlCompilationException(
                    "Simple CASE branches must preserve one canonical operand before native lowering.");
            }

            var match = Render(
                comparison.Right,
                provider,
                renderSubquery,
                dmlContext);
            var value = Render(
                branch.Value,
                provider,
                renderSubquery,
                dmlContext);
            parts.Add("WHEN " + match.Sql + " THEN " + value.Sql);
            bindings.AddRange(match.Bindings);
            bindings.AddRange(value.Bindings);
        }

        if (@case.ElseExpression is not null)
        {
            var otherwise = Render(
                @case.ElseExpression,
                provider,
                renderSubquery,
                dmlContext);
            parts.Add("ELSE " + otherwise.Sql);
            bindings.AddRange(otherwise.Bindings);
        }

        return new NativeSqlFragment(
            "CASE " + operand.Sql + " " + string.Join(" ", parts) + " END",
            bindings.ToImmutable());
    }

    private static BinaryExpr RequireSimpleCaseComparison(CaseBranch branch) =>
        branch.Condition is BinaryExpr { Operator: "=" } comparison
            ? comparison
            : throw new SqlCompilationException(
                "Simple CASE branch lost its canonical equality shape before lowering.");

    private static NativeSqlFragment RenderCase(
        CaseExpr @case,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        if (@case.Branches.IsDefaultOrEmpty)
            throw new SqlCompilationException("Searched CASE requires at least one WHEN branch.");

        var bindings = ImmutableArray.CreateBuilder<object?>();
        var parts = new List<string>();
        foreach (var branch in @case.Branches)
        {
            var condition = Render(
                branch.Condition,
                provider,
                renderSubquery,
                dmlContext);
            var value = Render(
                branch.Value,
                provider,
                renderSubquery,
                dmlContext);
            parts.Add("WHEN " + condition.Sql + " THEN " + value.Sql);
            bindings.AddRange(condition.Bindings);
            bindings.AddRange(value.Bindings);
        }

        if (@case.ElseExpression is not null)
        {
            var otherwise = Render(
                @case.ElseExpression,
                provider,
                renderSubquery,
                dmlContext);
            parts.Add("ELSE " + otherwise.Sql);
            bindings.AddRange(otherwise.Bindings);
        }

        return new NativeSqlFragment(
            "CASE " + string.Join(" ", parts) + " END",
            bindings.ToImmutable());
    }

    private static NativeSqlFragment RenderIn(
        InExpr @in,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        if (@in.Items.IsDefaultOrEmpty)
            throw new SqlCompilationException("IN requires at least one item.");

        var value = Render(
            @in.Value,
            provider,
            renderSubquery,
            dmlContext);
        var items = @in.Items
            .Select(item =>
                Render(item, provider, renderSubquery, dmlContext))
            .ToArray();
        var op = @in.IsNegated ? "NOT IN" : "IN";

        return new NativeSqlFragment(
            "(" + value.Sql + " " + op + " (" +
            string.Join(", ", items.Select(item => item.Sql)) + "))",
            value.Bindings
                .Concat(items.SelectMany(item => item.Bindings))
                .ToImmutableArray());
    }

    private static NativeSqlFragment RenderBetween(
        BetweenExpr between,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        var value = Render(
            between.Value,
            provider,
            renderSubquery,
            dmlContext);
        var lower = Render(
            between.Lower,
            provider,
            renderSubquery,
            dmlContext);
        var upper = Render(
            between.Upper,
            provider,
            renderSubquery,
            dmlContext);
        var op = between.IsNegated ? "NOT BETWEEN" : "BETWEEN";

        return new NativeSqlFragment(
            "(" + value.Sql + " " + op + " " + lower.Sql +
            " AND " + upper.Sql + ")",
            value.Bindings
                .Concat(lower.Bindings)
                .Concat(upper.Bindings)
                .ToImmutableArray());
    }

    private static NativeSqlFragment RenderIsNull(
        IsNullExpr isNull,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        var value = Render(
            isNull.Value,
            provider,
            renderSubquery,
            dmlContext);
        return value with
        {
            Sql = "(" + value.Sql + " IS " +
                  (isNull.IsNegated ? "NOT " : string.Empty) +
                  "NULL)"
        };
    }

    private static NativeSqlFragment RenderSubquery(
        SqlStatement statement,
        Func<SqlStatement, NativeSqlFragment> renderSubquery)
    {
        var subquery = renderSubquery(statement);
        return subquery with { Sql = "(" + subquery.Sql + ")" };
    }

    private static NativeSqlFragment RenderExists(
        ExistsExpr exists,
        Func<SqlStatement, NativeSqlFragment> renderSubquery)
    {
        var subquery = RenderSubquery(exists.Query, renderSubquery);
        return subquery with
        {
            Sql = (exists.IsNegated ? "NOT " : string.Empty) +
                  "EXISTS " + subquery.Sql
        };
    }

    private static NativeSqlFragment RenderIdentifier(
        SqlIdentifier identifier,
        SqlAgentToolType provider) =>
        new(
            CoreIdentifierSqlRenderer.Render(
                identifier,
                provider,
                allowWildcard: true),
            ImmutableArray<object?>.Empty);

    private static void RequireCurrentTemporalShape(
        FunctionCallExpr function)
    {
        if (function.IsDistinct || !function.Arguments.IsDefaultOrEmpty)
        {
            throw new SqlCompilationException(
                "Canonical current temporal function '" +
                IdentifierText(function.Name) +
                "' must have zero arguments and cannot be DISTINCT.");
        }
    }

    private static void RequireArguments(
        FunctionCallExpr function,
        int count)
    {
        if (function.Arguments.Length != count)
        {
            throw new SqlCompilationException(
                "Canonical function '" + IdentifierText(function.Name) +
                "' requires " + count + " argument(s).");
        }
    }

    private static string LiteralKeyword(
        SqlExpr expression,
        string label)
    {
        if (expression is not LiteralExpr { Value: string value })
        {
            throw new SqlCompilationException(
                label + " must be a canonical literal keyword.");
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(
                normalized,
                "^[A-Z_]+$",
                RegexOptions.CultureInvariant))
        {
            throw new SqlCompilationException(
                "Unsafe " + label + " '" + value + "'.");
        }

        return normalized;
    }

    private static string StringLiteralValue(
        SqlExpr expression,
        string label)
    {
        if (expression is not LiteralExpr { Value: string value })
            throw new SqlCompilationException(label + " must be a string literal.");

        return value;
    }

    private static string SqlStringLiteral(
        SqlExpr expression,
        string label,
        SqlAgentToolType provider)
    {
        if (expression is not LiteralExpr { Value: string value })
            throw new SqlCompilationException(label + " must be a string literal.");

        // MySQL interprets backslash escape sequences unless NO_BACKSLASH_ESCAPES is active.
        // Core cannot assume a target session mode at this low-level rendering boundary, and
        // GROUP_CONCAT SEPARATOR cannot use a bound parameter. Use a hexadecimal string
        // literal containing the UTF-8 bytes whenever the value contains a backslash or control
        // character so the emitted value is independent of sql_mode and cannot change lexical structure.
        if (provider == SqlAgentToolType.MySQL
            && value.Any(character => character == '\\' || char.IsControl(character)))
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return "0x" + Convert.ToHexString(bytes);
        }

        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static IReadOnlyList<string> JsonPathSegments(
        SqlExpr expression)
    {
        if (expression is not LiteralExpr { Value: string path })
        {
            throw new SqlCompilationException(
                "JSON path must be a string literal for structured PostgreSQL lowering.");
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('$'))
            throw new SqlCompilationException("Unsupported JSON path '" + path + "'.");

        var remainder = trimmed[1..].TrimStart('.');
        if (string.IsNullOrEmpty(remainder))
            return [];

        var segments = remainder.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (segments.Any(segment => !Regex.IsMatch(
                segment,
                "^[A-Za-z_][A-Za-z0-9_]*$",
                RegexOptions.CultureInvariant)))
        {
            throw new SqlCompilationException(
                "Unsupported structured JSON path '" + path + "'.");
        }

        return segments;
    }

    private static bool EquivalentFragment(
        NativeSqlFragment left,
        NativeSqlFragment right)
    {
        if (!string.Equals(left.Sql, right.Sql, StringComparison.Ordinal)
            || left.Bindings.Length != right.Bindings.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Bindings.Length; i++)
        {
            if (!Equals(left.Bindings[i], right.Bindings[i]))
                return false;
        }

        return true;
    }

    private static NativeSqlFragment Combine(
        string sql,
        NativeSqlFragment left,
        NativeSqlFragment right) =>
        new(
            sql,
            left.Bindings.Concat(right.Bindings).ToImmutableArray());

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
