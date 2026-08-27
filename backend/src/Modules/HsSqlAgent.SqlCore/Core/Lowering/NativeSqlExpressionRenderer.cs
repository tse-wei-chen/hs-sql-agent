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
internal static partial class NativeSqlExpressionRenderer
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

    public static NativeSqlFragment RenderPredicate(
        SqlExpr expression,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext = false)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(renderSubquery);

        if (provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer)
        {
            if (expression is LiteralExpr { Value: bool boolean })
            {
                return new NativeSqlFragment(
                    boolean ? "(1 = 1)" : "(1 = 0)",
                    ImmutableArray<object?>.Empty);
            }

            if (expression is UnaryExpr { Operator: "NOT" } unary)
            {
                var operand = RenderPredicate(
                    unary.Operand,
                    provider,
                    renderSubquery,
                    dmlContext);
                return operand with
                {
                    Sql = "NOT (" + operand.Sql + ")"
                };
            }

            if (expression is BinaryExpr binary
                && binary.Operator is "AND" or "OR")
            {
                var left = RenderPredicate(
                    binary.Left,
                    provider,
                    renderSubquery,
                    dmlContext);
                var right = RenderPredicate(
                    binary.Right,
                    provider,
                    renderSubquery,
                    dmlContext);
                return Combine(
                    "(" + left.Sql + " " + binary.Operator + " " + right.Sql + ")",
                    left,
                    right);
            }

            if (expression is CaseExpr @case
                && CoreBooleanProjectionRules.IsDefinitelyBoolean(@case, provider))
            {
                return @case is SimpleCaseExpr simpleCase
                    ? RenderBooleanSimpleCasePredicate(
                        simpleCase,
                        provider,
                        renderSubquery,
                        dmlContext)
                    : RenderBooleanCasePredicate(
                        @case,
                        provider,
                        renderSubquery,
                        dmlContext);
            }
        }

        return Render(
            expression,
            provider,
            renderSubquery,
            dmlContext);
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
        if (binary.Operator is "IN" or "NOT IN"
            && binary.Right is not SubqueryExpr)
        {
            throw new SqlCompilationException(
                "Canonical binary IN/NOT IN requires a scalar subquery RHS; expression lists must use InExpr.");
        }

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

    private static NativeSqlFragment RenderBooleanSimpleCasePredicate(
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
            var value = RenderBooleanTruthValue(
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
            var otherwise = RenderBooleanTruthValue(
                @case.ElseExpression,
                provider,
                renderSubquery,
                dmlContext);
            parts.Add("ELSE " + otherwise.Sql);
            bindings.AddRange(otherwise.Bindings);
        }

        return new NativeSqlFragment(
            "(CASE " + operand.Sql + " " + string.Join(" ", parts) + " END = 1)",
            bindings.ToImmutable());
    }

    private static NativeSqlFragment RenderBooleanCasePredicate(
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
            var condition = RenderPredicate(
                branch.Condition,
                provider,
                renderSubquery,
                dmlContext);
            var value = RenderBooleanTruthValue(
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
            var otherwise = RenderBooleanTruthValue(
                @case.ElseExpression,
                provider,
                renderSubquery,
                dmlContext);
            parts.Add("ELSE " + otherwise.Sql);
            bindings.AddRange(otherwise.Bindings);
        }

        return new NativeSqlFragment(
            "(CASE " + string.Join(" ", parts) + " END = 1)",
            bindings.ToImmutable());
    }

    private static NativeSqlFragment RenderBooleanTruthValue(
        SqlExpr expression,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        bool dmlContext)
    {
        if (expression is LiteralExpr { Value: null })
        {
            return new NativeSqlFragment(
                "NULL",
                ImmutableArray<object?>.Empty);
        }

        var predicate = RenderPredicate(
            expression,
            provider,
            renderSubquery,
            dmlContext);
        return predicate with
        {
            Sql = "CASE WHEN " + predicate.Sql + " THEN 1 ELSE 0 END"
        };
    }

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
            var condition = RenderPredicate(
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
        ValidateScalarSubqueryProjection(statement);
        var subquery = renderSubquery(statement);
        return subquery with { Sql = "(" + subquery.Sql + ")" };
    }

    private static NativeSqlFragment RenderExists(
        ExistsExpr exists,
        Func<SqlStatement, NativeSqlFragment> renderSubquery)
    {
        var subquery = renderSubquery(exists.Query);
        return subquery with
        {
            Sql = (exists.IsNegated ? "NOT " : string.Empty) +
                  "EXISTS (" + subquery.Sql + ")"
        };
    }

    private static void ValidateScalarSubqueryProjection(SqlStatement statement)
    {
        var projection = statement switch
        {
            SelectStatement select => select.Select,
            QueryStatement query => query.Head.Select,
            _ => throw new SqlCompilationException(
                "Scalar subquery must contain a SELECT-compatible query statement.")
        };

        if (projection.Length != 1
            || IsDirectProjectionWildcard(projection[0].Expression))
        {
            throw new SqlCompilationException(
                "Scalar subquery must expose exactly one statically known output column.");
        }
    }

    private static bool IsDirectProjectionWildcard(SqlExpr expression)
    {
        var identifier = expression switch
        {
            ColumnExpr column => column.Name,
            BoundColumnExpr column => column.Name,
            _ => null
        };
        return identifier is not null
            && !identifier.Parts.IsDefaultOrEmpty
            && identifier.Parts[^1].Value == "*"
            && !identifier.Parts[^1].WasQuoted;
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
