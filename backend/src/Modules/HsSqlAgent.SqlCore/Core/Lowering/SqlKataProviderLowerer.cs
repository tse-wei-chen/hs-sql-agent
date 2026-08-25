using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlKata;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Lowers the provider-neutral Core AST into the existing SqlKata backend. Statement structure is
/// represented by SqlKata Query nodes. Expression raw fragments are generated only from closed
/// Core node/operator/function semantics; identifiers are quoted by the target compiler and user
/// values remain bindings.
/// </summary>
public sealed class SqlKataProviderLowerer(SqlAgentToolType provider) : IProviderLowerer
{
    private static readonly Regex SafeCastType = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*(?:\((?:MAX|[0-9]+(?:,[0-9]+)?)\))?(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public SqlAgentToolType Provider { get; } = provider;

    public CompiledSqlCommand Lower(ExecutableSqlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.TargetProvider != Provider)
            throw new SqlCompilationException(
                $"Plan targets {plan.TargetProvider}, but this lowerer targets {Provider}.");

        var compiler = CreateCompiler(Provider);
        var query = BuildQuery(plan.Statement, compiler);
        var result = compiler.Compile(query);
        var parameters = result.NamedBindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => new SqlParameterValue(pair.Key, NormalizeBindingValue(pair.Value)))
            .ToImmutableArray();

        return new CompiledSqlCommand(
            result.Sql,
            parameters,
            SqlStatementKind.Select,
            ComputePlanFingerprint(result.Sql, parameters, plan),
            Provider);
    }

    internal static Query BuildQuery(SqlStatement statement, Compiler compiler) => statement switch
    {
        SelectStatement select => LowerSelect(select, compiler),
        QueryStatement query => LowerQuery(query, compiler),
        _ => throw new SqlCompilationException(
            $"Unsupported statement during SqlKata query building: {statement.GetType().Name}")
    };

    private static Query LowerQuery(QueryStatement statement, Compiler compiler)
    {
        var setQuery = LowerSelect(statement.Head, compiler, includeTail: false);
        foreach (var operation in statement.SetOperations)
        {
            var branch = BuildQuery(operation.Query, compiler);
            setQuery = operation.Kind switch
            {
                SetOperationKind.Union => setQuery.Union(branch),
                SetOperationKind.UnionAll => setQuery.UnionAll(branch),
                SetOperationKind.Intersect => setQuery.Intersect(branch),
                SetOperationKind.Except => setQuery.Except(branch),
                _ => throw new SqlCompilationException(
                    $"Unsupported set operation '{operation.Kind}'.")
            };
        }

        if (statement.OrderBy.IsDefaultOrEmpty
            && statement.Limit is null
            && statement.Offset is not > 0)
            return setQuery;

        var query = new Query()
            .From(setQuery, "_set")
            .Select("*");
        ApplyOrderBy(query, statement.OrderBy, compiler, statement.Head.Select);
        if (statement.Limit is >= 0) query.Limit(statement.Limit.Value);
        if (statement.Offset is > 0) query.Offset(statement.Offset.Value);
        return query;
    }

    private static Query LowerSelect(
        SelectStatement statement,
        Compiler compiler,
        bool includeTail = true)
    {
        var query = new Query();

        foreach (var cte in statement.Ctes)
        {
            if (!cte.ColumnAliases.IsDefaultOrEmpty)
                throw new SqlCompilationException("CTE column aliases are not yet supported by the Core SqlKata lowerer.");
            query.With(CteName(cte.Name, compiler), BuildQuery(cte.Query, compiler));
        }

        if (statement.From is not null)
            ApplyFrom(query, statement.From, compiler);

        if (statement.Distinct) query.Distinct();

        foreach (var item in statement.Select)
        {
            if (item.Expression is SubqueryExpr subquery)
            {
                var renderedSubquery = RenderSubquery(subquery.Query, compiler);
                var expression = renderedSubquery.Sql;
                if (item.Alias is not null)
                    expression += $" AS {RenderAlias(item.Alias, compiler)}";
                query.Select(new RawColumn
                {
                    Expression = expression,
                    Bindings = renderedSubquery.Bindings.ToArray()
                });
                continue;
            }

            var rendered = RenderExpression(item.Expression, compiler);
            var renderedSql = rendered.Sql;
            if (item.Alias is not null)
                renderedSql += $" AS {RenderAlias(item.Alias, compiler)}";
            query.Select(new RawColumn
            {
                Expression = renderedSql,
                Bindings = rendered.Bindings.ToArray()
            });
        }

        foreach (var join in statement.Joins)
            ApplyJoin(query, join, compiler);

        if (statement.Where is not null)
        {
            var predicate = RenderExpression(statement.Where, compiler);
            query.WhereRaw(predicate.Sql, predicate.Bindings.ToArray());
        }

        foreach (var expression in statement.GroupBy)
        {
            var rendered = RenderExpression(expression, compiler);
            query.GroupBy(new RawColumn
            {
                Expression = rendered.Sql,
                Bindings = rendered.Bindings.ToArray()
            });
        }

        if (statement.Having is not null)
        {
            var having = RenderExpression(statement.Having, compiler);
            query.AddComponent("having", new RawCondition
            {
                Expression = having.Sql,
                Bindings = having.Bindings.ToArray()
            });
        }

        if (includeTail)
        {
            ApplyOrderBy(query, statement.OrderBy, compiler, statement.Select);
            if (statement.Limit is >= 0) query.Limit(statement.Limit.Value);
            if (statement.Offset is > 0) query.Offset(statement.Offset.Value);
        }

        return query;
    }

    private static void ApplyFrom(Query query, TableSource source, Compiler compiler)
    {
        switch (source)
        {
            case NamedTableSource named:
                query.FromRaw(RenderNamedTableSource(named, compiler));
                return;
            case DerivedTableSource derived:
                query.From(BuildQuery(derived.Query, compiler), AliasText(derived.Alias, compiler));
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported FROM source during lowering: {source.GetType().Name}");
        }
    }

    private static void ApplyJoin(Query query, JoinSource join, Compiler compiler)
    {
        var type = join.Kind switch
        {
            "INNER" => "inner join",
            "LEFT" => "left join",
            "RIGHT" => "right join",
            "FULL" => "full outer join",
            "CROSS" => "cross join",
            _ => throw new SqlCompilationException($"Unsupported JOIN kind '{join.Kind}'.")
        };

        if (join.Kind == "CROSS")
        {
            if (join.Predicate is not null)
                throw new SqlCompilationException("CROSS JOIN cannot have an ON predicate.");
            switch (join.Source)
            {
                case NamedTableSource named:
                    AddNamedJoin(query, named, type, predicate: null, compiler);
                    return;
                case DerivedTableSource derived:
                    query.Join(
                        BuildQuery(derived.Query, compiler).As(AliasText(derived.Alias, compiler)),
                        j => j,
                        type);
                    return;
                default:
                    throw new SqlCompilationException(
                        $"Unsupported CROSS JOIN source '{join.Source.GetType().Name}'.");
            }
        }

        if (join.Predicate is null)
            throw new SqlCompilationException($"{join.Kind} JOIN requires an ON predicate.");
        var predicate = RenderExpression(join.Predicate, compiler);

        switch (join.Source)
        {
            case NamedTableSource named:
                AddNamedJoin(query, named, type, predicate, compiler);
                return;
            case DerivedTableSource derived:
                query.Join(
                    BuildQuery(derived.Query, compiler).As(AliasText(derived.Alias, compiler)),
                    j => j.WhereRaw(predicate.Sql, predicate.Bindings.ToArray()),
                    type);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported JOIN source '{join.Source.GetType().Name}'.");
        }
    }

    private static void AddNamedJoin(
        Query query,
        NamedTableSource source,
        string type,
        RenderedExpression? predicate,
        Compiler compiler)
    {
        var backendJoin = new Join()
            .FromRaw(RenderNamedTableSource(source, compiler))
            .AsType(type);
        if (predicate is not null)
            backendJoin.WhereRaw(predicate.Sql, predicate.Bindings.ToArray());
        query.AddComponent("join", new BaseJoin { Join = backendJoin });
    }

    private static void ApplyOrderBy(
        Query query,
        IEnumerable<OrderByItem> items,
        Compiler compiler,
        IEnumerable<SelectItem>? projection = null)
    {
        var preservedAliases = projection?
            .Select(item => item.Alias)
            .Where(alias => alias is { PreserveSpelling: true })
            .Cast<IdentifierPart>()
            .ToArray() ?? [];

        foreach (var item in items)
        {
            var rendered = TryRenderPreservedProjectionAlias(
                    item.Expression,
                    preservedAliases,
                    compiler)
                ?? RenderExpression(item.Expression, compiler);
            var nullOrdering = item.NullOrdering switch
            {
                NullOrderingKind.Default => string.Empty,
                NullOrderingKind.First => "first",
                NullOrderingKind.Last => "last",
                _ => throw new SqlCompilationException(
                    $"Unsupported NULL ordering '{item.NullOrdering}'.")
            };
            query.OrderBy(
                new RawColumn
                {
                    Expression = rendered.Sql,
                    Bindings = rendered.Bindings.ToArray()
                },
                !item.Descending,
                nullOrdering);
        }
    }

    private static RenderedExpression? TryRenderPreservedProjectionAlias(
        SqlExpr expression,
        IReadOnlyCollection<IdentifierPart> aliases,
        Compiler compiler)
    {
        var identifier = expression switch
        {
            BoundColumnExpr bound => bound.Name,
            ColumnExpr column => column.Name,
            _ => null
        };
        if (identifier is not { Parts.Length: 1 }) return null;

        var reference = identifier.Parts[0];
        if (reference.WasQuoted) return null;
        var matches = aliases
            .Where(alias => string.Equals(
                alias.Value,
                reference.Value,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
        {
            throw new SqlCompilationException(
                $"ORDER BY alias '{reference.Value}' is ambiguous among preserved projection aliases.");
        }
        if (matches.Length == 0) return null;
        return new RenderedExpression(
            RenderAlias(matches[0], compiler),
            ImmutableArray<object?>.Empty);
    }

    private static RenderedExpression RenderExpression(SqlExpr expression, Compiler compiler)
    {
        return expression switch
        {
            BoundColumnExpr column => RenderIdentifier(column.Name, compiler),
            ColumnExpr column => RenderIdentifier(column.Name, compiler),
            LiteralExpr literal => RenderLiteral(literal, compiler),
            IntervalExpr interval => RenderInterval(interval, compiler),
            UnaryExpr unary => RenderUnary(unary, compiler),
            BinaryExpr binary => RenderBinary(binary, compiler),
            FunctionCallExpr function => RenderFunction(function, compiler),
            FilterExpr filter => RenderFilter(filter, compiler),
            WindowedExpr windowed => RenderWindowed(windowed, compiler),
            CastExpr cast => RenderCast(cast, compiler),
            SimpleCaseExpr simpleCase => RenderSimpleCase(simpleCase, compiler),
            CaseExpr @case => RenderCase(@case, compiler),
            InExpr @in => RenderIn(@in, compiler),
            BetweenExpr between => RenderBetween(between, compiler),
            IsNullExpr isNull => RenderIsNull(isNull, compiler),
            SubqueryExpr subquery => RenderSubquery(subquery.Query, compiler),
            ExistsExpr exists => RenderExists(exists, compiler),
            _ => throw new SqlCompilationException(
                $"Unsupported expression during SqlKata lowering: {expression.GetType().Name}")
        };
    }

    private static RenderedExpression RenderLiteral(LiteralExpr literal, Compiler compiler)
    {
        if (literal.Value is SqlTimeValue && compiler is OracleCompiler)
            throw new SqlCompilationException("Oracle has no standalone TIME data type.");

        if (literal.Value is SqlOffsetDateTimeValue && compiler is MySqlCompiler)
            throw new SqlCompilationException("MySQL has no native timestamp type that preserves a UTC offset.");

        if (literal.Value is SqlOffsetDateTimeValue postgresOffset && compiler is PostgresCompiler)
            return new RenderedExpression("?", [postgresOffset.Value.ToUniversalTime()]);

        if (literal.Value is DateTimeOffset postgresRawOffset && compiler is PostgresCompiler)
            return new RenderedExpression("?", [postgresRawOffset.ToUniversalTime()]);

        var value = NormalizeBindingValue(literal.Value);
        if (compiler is FirebirdCompiler)
        {
            return literal.Value switch
            {
                SqlDateValue => new RenderedExpression("CAST(? AS DATE)", [value]),
                SqlTimeValue => new RenderedExpression("CAST(? AS TIME)", [value]),
                SqlLocalDateTimeValue => new RenderedExpression("CAST(? AS TIMESTAMP)", [value]),
                SqlOffsetDateTimeValue offset => new RenderedExpression(
                    "CAST(? AS TIMESTAMP WITH TIME ZONE)",
                    [FormatFirebirdOffsetTimestamp(offset.Value)]),
                _ => value switch
                {
                    DateOnly => new RenderedExpression("CAST(? AS DATE)", [value]),
                    TimeOnly or TimeSpan => new RenderedExpression("CAST(? AS TIME)", [value]),
                    DateTime => new RenderedExpression("CAST(? AS TIMESTAMP)", [value]),
                    DateTimeOffset offset => new RenderedExpression(
                        "CAST(? AS TIMESTAMP WITH TIME ZONE)",
                        [FormatFirebirdOffsetTimestamp(offset)]),
                    string text => RenderFirebirdString(text),
                    bool => new RenderedExpression("CAST(? AS BOOLEAN)", [value]),
                    byte or sbyte or short or ushort or int => new RenderedExpression("CAST(? AS INTEGER)", [value]),
                    uint or long => new RenderedExpression("CAST(? AS BIGINT)", [value]),
                    decimal => new RenderedExpression("CAST(? AS DECIMAL(38,10))", [value]),
                    double or float => new RenderedExpression("CAST(? AS DOUBLE PRECISION)", [value]),
                    _ => new RenderedExpression("?", [value])
                }
            };
        }
        return new RenderedExpression("?", [value]);
    }

    private static RenderedExpression RenderFirebirdString(string value)
    {
        const int maxFirebirdUtf8VarcharChars = 8191;
        if (value.Length > maxFirebirdUtf8VarcharChars)
            throw new SqlCompilationException(
                $"Firebird string literal exceeds the safe UTF8 VARCHAR limit of {maxFirebirdUtf8VarcharChars} characters.");

        var length = Math.Max(1, value.Length);
        return new RenderedExpression($"CAST(? AS VARCHAR({length}))", [value]);
    }

    private static string FormatFirebirdOffsetTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture);

    private static RenderedExpression RenderInterval(IntervalExpr interval, Compiler compiler)
    {
        if (compiler is not PostgresCompiler)
            throw new SqlCompilationException("INTERVAL expressions are supported only by PostgreSQL in the Core backend.");
        var literal = interval.Literal.Replace("'", "''", StringComparison.Ordinal);
        return new RenderedExpression($"INTERVAL '{literal}'", ImmutableArray<object?>.Empty);
    }

    private static RenderedExpression RenderUnary(UnaryExpr unary, Compiler compiler)
    {
        if (unary.Operator != "NOT")
            throw new SqlCompilationException($"Unsupported unary operator '{unary.Operator}'.");
        var operand = RenderExpression(unary.Operand, compiler);
        return operand with { Sql = $"NOT ({operand.Sql})" };
    }

    private static RenderedExpression RenderBinary(BinaryExpr binary, Compiler compiler)
    {
        var left = RenderExpression(binary.Left, compiler);
        var right = RenderExpression(binary.Right, compiler);
        var likeEscape = CoreLikeEscapeSqlRenderer.RenderSuffix(binary, compiler);

        if (binary.Operator == "%" && compiler is OracleCompiler or FirebirdCompiler)
            return Combine($"MOD({left.Sql}, {right.Sql})", left, right);
        if (binary.Operator == "||" && compiler is MySqlCompiler)
            return Combine($"CONCAT({left.Sql}, {right.Sql})", left, right);
        if (binary.Operator == "||" && compiler is SqlServerCompiler)
            return Combine($"({left.Sql} + {right.Sql})", left, right);

        var op = binary.Operator switch
        {
            "+" or "-" or "*" or "/" or "%" or "||" or
            "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=" or
            "LIKE" or "ILIKE" or "AND" or "OR" or "IN" or "NOT IN" => binary.Operator,
            _ => throw new SqlCompilationException($"Unsupported binary operator '{binary.Operator}'.")
        };
        return Combine($"({left.Sql} {op} {right.Sql}{likeEscape})", left, right);
    }

    private static RenderedExpression RenderFunction(FunctionCallExpr function, Compiler compiler)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        return name switch
        {
            "CORE_DATE_ADD" => RenderDateAdd(function, compiler),
            "CORE_DATE_DIFF" => RenderDateDiff(function, compiler),
            "CORE_DATE_PART" => RenderDatePart(function, compiler),
            "CORE_DATE_FORMAT" => RenderDateFormat(function, compiler),
            "CORE_DATE_PARSE" => RenderDateParse(function, compiler),
            "CORE_POSITION" => RenderPosition(function, compiler),
            "CORE_JSON_EXTRACT" => RenderJsonExtract(function, compiler),
            "CORE_JSON_SET" => RenderJsonSet(function, compiler),
            "CORE_REGEX_MATCH" => RenderRegexMatch(function, compiler),
            "CORE_CURRENT_DATE" => RenderCurrentDate(function, compiler),
            "CORE_CURRENT_TIME" => RenderCurrentTime(function, compiler),
            "CORE_CURRENT_TIMESTAMP" => RenderCurrentTimestamp(function),
            "CORE_STRING_AGG" => RenderStringAggregate(function, compiler),
            _ => RenderOrdinaryFunction(function, compiler)
        };
    }

    private static RenderedExpression RenderOrdinaryFunction(FunctionCallExpr function, Compiler compiler)
    {
        var name = IdentifierText(function.Name);
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new SqlCompilationException($"Unsafe function identifier '{name}'.");

        var args = function.Arguments.Select(arg => RenderExpression(arg, compiler)).ToArray();
        var renderedArgs = args.Select(arg => arg.Sql).ToArray();
        if (compiler is PostgresCompiler
            && name.Equals("ROUND", StringComparison.OrdinalIgnoreCase)
            && args.Length == 2)
        {
            renderedArgs[0] = $"CAST({renderedArgs[0]} AS numeric)";
        }

        var sql = string.Join(", ", renderedArgs);
        if (function.IsDistinct) sql = "DISTINCT " + sql;
        return new RenderedExpression(
            $"{name}({sql})",
            args.SelectMany(arg => arg.Bindings).ToImmutableArray());
    }

    private static RenderedExpression RenderDateAdd(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 3);
        var unit = LiteralKeyword(function.Arguments[0], "DATEADD unit");
        if (unit != "DAY" && compiler is PostgresCompiler or OracleCompiler or SqliteCompiler)
            throw new SqlCompilationException($"DATEADD unit {unit} is not supported by {compiler.GetType().Name}.");
        var amount = RenderExpression(function.Arguments[1], compiler);
        var value = RenderExpression(function.Arguments[2], compiler);
        return compiler switch
        {
            SqlServerCompiler => Combine($"DATEADD({unit}, {amount.Sql}, {value.Sql})", amount, value),
            MySqlCompiler => Combine($"TIMESTAMPADD({unit}, {amount.Sql}, {value.Sql})", amount, value),
            PostgresCompiler => Combine($"({value.Sql} + ({amount.Sql} * INTERVAL '1 day'))", value, amount),
            OracleCompiler => Combine($"({value.Sql} + {amount.Sql})", value, amount),
            SqliteCompiler => Combine($"DATETIME({value.Sql}, PRINTF('%+d day', {amount.Sql}))", value, amount),
            FirebirdCompiler => Combine($"DATEADD({unit}, {amount.Sql}, {value.Sql})", amount, value),
            _ => throw new SqlCompilationException("Unsupported DATEADD provider.")
        };
    }

    private static RenderedExpression RenderDateDiff(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 3);
        var unit = LiteralKeyword(function.Arguments[0], "DATEDIFF unit");
        if (unit != "DAY" && compiler is PostgresCompiler or OracleCompiler or SqliteCompiler)
            throw new SqlCompilationException($"DATEDIFF unit {unit} is not supported by {compiler.GetType().Name}.");
        var start = RenderExpression(function.Arguments[1], compiler);
        var end = RenderExpression(function.Arguments[2], compiler);
        return compiler switch
        {
            SqlServerCompiler => Combine($"DATEDIFF({unit}, {start.Sql}, {end.Sql})", start, end),
            MySqlCompiler => Combine($"TIMESTAMPDIFF({unit}, {start.Sql}, {end.Sql})", start, end),
            PostgresCompiler => Combine(
                $"(CAST({end.Sql} AS date) - CAST({start.Sql} AS date))",
                end,
                start),
            OracleCompiler => Combine(
                $"(CAST({end.Sql} AS DATE) - CAST({start.Sql} AS DATE))",
                end,
                start),
            SqliteCompiler => Combine($"(JULIANDAY({end.Sql}) - JULIANDAY({start.Sql}))", end, start),
            FirebirdCompiler => Combine($"DATEDIFF({unit} FROM {start.Sql} TO {end.Sql})", start, end),
            _ => throw new SqlCompilationException("Unsupported DATEDIFF provider.")
        };
    }

    private static RenderedExpression RenderDatePart(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var part = LiteralKeyword(function.Arguments[0], "date part");
        var value = RenderExpression(function.Arguments[1], compiler);
        var sql = compiler switch
        {
            SqlServerCompiler or MySqlCompiler => $"{part}({value.Sql})",
            PostgresCompiler or OracleCompiler => $"EXTRACT({part} FROM {value.Sql})",
            FirebirdCompiler => $"EXTRACT({part} FROM CAST({value.Sql} AS DATE))",
            SqliteCompiler => part switch
            {
                "YEAR" => $"CAST(STRFTIME('%Y', {value.Sql}) AS INTEGER)",
                "MONTH" => $"CAST(STRFTIME('%m', {value.Sql}) AS INTEGER)",
                "DAY" => $"CAST(STRFTIME('%d', {value.Sql}) AS INTEGER)",
                _ => throw new SqlCompilationException($"SQLite does not support date part {part}.")
            },
            _ => throw new SqlCompilationException("Unsupported date-part provider.")
        };
        return value with { Sql = sql };
    }

    private static RenderedExpression RenderDateFormat(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var value = RenderExpression(function.Arguments[0], compiler);
        var format = SqlStringLiteral(function.Arguments[1], "date format");
        var sql = compiler switch
        {
            SqlServerCompiler => $"FORMAT({value.Sql}, {format})",
            PostgresCompiler or OracleCompiler => $"TO_CHAR({value.Sql}, {format})",
            MySqlCompiler => $"DATE_FORMAT({value.Sql}, {format})",
            SqliteCompiler => $"STRFTIME({format}, {value.Sql})",
            FirebirdCompiler => throw new SqlCompilationException("portable date formatting is not supported by Firebird."),
            _ => throw new SqlCompilationException("Unsupported date-format provider.")
        };
        return value with { Sql = sql };
    }

    private static RenderedExpression RenderDateParse(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var value = RenderExpression(function.Arguments[0], compiler);
        var format = SqlStringLiteral(function.Arguments[1], "date parse format");
        var sql = compiler switch
        {
            MySqlCompiler => $"DATE(STR_TO_DATE({value.Sql}, {format}))",
            PostgresCompiler or OracleCompiler => $"TO_DATE({value.Sql}, {format})",
            _ => throw new SqlCompilationException("formatted date parsing is not supported by this provider.")
        };
        return value with { Sql = sql };
    }

    private static RenderedExpression RenderPosition(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var haystack = RenderExpression(function.Arguments[0], compiler);
        var needle = RenderExpression(function.Arguments[1], compiler);
        return compiler switch
        {
            SqlServerCompiler => Combine($"CHARINDEX({needle.Sql}, {haystack.Sql})", needle, haystack),
            PostgresCompiler => Combine($"STRPOS({haystack.Sql}, {needle.Sql})", haystack, needle),
            MySqlCompiler => Combine($"LOCATE({needle.Sql}, {haystack.Sql})", needle, haystack),
            SqliteCompiler or OracleCompiler => Combine($"INSTR({haystack.Sql}, {needle.Sql})", haystack, needle),
            FirebirdCompiler => Combine($"POSITION({needle.Sql}, {haystack.Sql})", needle, haystack),
            _ => throw new SqlCompilationException("Unsupported position provider.")
        };
    }

    private static RenderedExpression RenderJsonExtract(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var value = RenderExpression(function.Arguments[0], compiler);
        var path = RenderExpression(function.Arguments[1], compiler);
        if (compiler is MySqlCompiler or SqliteCompiler)
            return Combine($"JSON_EXTRACT({value.Sql}, {path.Sql})", value, path);
        if (compiler is not PostgresCompiler)
            throw new SqlCompilationException("JSON_EXTRACT is not supported losslessly by this provider.");

        var segments = JsonPathSegments(function.Arguments[1]);
        var bindings = value.Bindings.ToBuilder();
        var placeholders = new List<string>();
        foreach (var segment in segments)
        {
            placeholders.Add("?");
            bindings.Add(segment);
        }
        return new RenderedExpression(
            $"JSONB_EXTRACT_PATH(CAST({value.Sql} AS jsonb), {string.Join(", ", placeholders)})",
            bindings.ToImmutable());
    }

    private static RenderedExpression RenderJsonSet(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 3);
        var value = RenderExpression(function.Arguments[0], compiler);
        var path = RenderExpression(function.Arguments[1], compiler);
        var newValue = RenderExpression(function.Arguments[2], compiler);
        if (compiler is MySqlCompiler or SqliteCompiler)
            return new RenderedExpression(
                $"JSON_SET({value.Sql}, {path.Sql}, {newValue.Sql})",
                value.Bindings.Concat(path.Bindings).Concat(newValue.Bindings).ToImmutableArray());
        if (compiler is SqlServerCompiler)
            return new RenderedExpression(
                $"JSON_MODIFY({value.Sql}, {path.Sql}, {newValue.Sql})",
                value.Bindings.Concat(path.Bindings).Concat(newValue.Bindings).ToImmutableArray());
        if (compiler is not PostgresCompiler)
            throw new SqlCompilationException("JSON_SET is not supported by this provider.");

        var pgPath = "{" + string.Join(',', JsonPathSegments(function.Arguments[1])) + "}";
        return new RenderedExpression(
            $"JSONB_SET(CAST({value.Sql} AS jsonb), CAST(? AS text[]), TO_JSONB({newValue.Sql}))",
            value.Bindings.Concat([pgPath]).Concat(newValue.Bindings).ToImmutableArray());
    }

    private static RenderedExpression RenderRegexMatch(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        if (compiler is SqlServerCompiler or SqliteCompiler or FirebirdCompiler)
            throw new SqlCompilationException("REGEXP_LIKE is not supported by this provider.");
        var value = RenderExpression(function.Arguments[0], compiler);
        var pattern = RenderExpression(function.Arguments[1], compiler);
        return compiler switch
        {
            PostgresCompiler => Combine($"({value.Sql} ~ {pattern.Sql})", value, pattern),
            _ => Combine($"REGEXP_LIKE({value.Sql}, {pattern.Sql})", value, pattern)
        };
    }

    private static RenderedExpression RenderCurrentDate(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 0);
        return new RenderedExpression(
            compiler is SqlServerCompiler ? "CAST(CURRENT_TIMESTAMP AS date)" : "CURRENT_DATE",
            ImmutableArray<object?>.Empty);
    }

    private static RenderedExpression RenderCurrentTime(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 0);
        if (compiler is OracleCompiler)
            throw new SqlCompilationException("CURRENT_TIME is not supported by Oracle.");
        return new RenderedExpression(
            compiler is SqlServerCompiler ? "CAST(CURRENT_TIMESTAMP AS time)" : "CURRENT_TIME",
            ImmutableArray<object?>.Empty);
    }

    private static RenderedExpression RenderCurrentTimestamp(FunctionCallExpr function)
    {
        RequireArguments(function, 0);
        return new RenderedExpression("CURRENT_TIMESTAMP", ImmutableArray<object?>.Empty);
    }

    private static RenderedExpression RenderStringAggregate(FunctionCallExpr function, Compiler compiler)
    {
        RequireArguments(function, 2);
        var value = RenderExpression(function.Arguments[0], compiler);
        var separator = SqlStringLiteral(function.Arguments[1], "string aggregate separator");
        var sql = compiler switch
        {
            SqlServerCompiler or PostgresCompiler => $"STRING_AGG({value.Sql}, {separator})",
            MySqlCompiler or SqliteCompiler => $"GROUP_CONCAT({value.Sql}, {separator})",
            OracleCompiler => $"LISTAGG({value.Sql}, {separator})",
            FirebirdCompiler => $"LIST({value.Sql}, {separator})",
            _ => throw new SqlCompilationException("Unsupported string aggregate provider.")
        };
        return value with { Sql = sql };
    }

    private static RenderedExpression RenderFilter(FilterExpr filter, Compiler compiler)
    {
        if (compiler is not (PostgresCompiler or SqliteCompiler or FirebirdCompiler))
            throw new SqlCompilationException(
                $"FILTER lowering is not supported by {compiler.GetType().Name}.");
        var expression = RenderExpression(filter.Expression, compiler);
        var predicate = RenderExpression(filter.Predicate, compiler);
        return new RenderedExpression(
            $"{expression.Sql} FILTER (WHERE {predicate.Sql})",
            expression.Bindings.Concat(predicate.Bindings).ToImmutableArray());
    }

    private static RenderedExpression RenderWindowed(WindowedExpr windowed, Compiler compiler)
    {
        var expression = RenderExpression(windowed.Expression, compiler);
        var parts = new List<string>();
        var bindings = ImmutableArray.CreateBuilder<object?>();
        bindings.AddRange(expression.Bindings);

        if (!windowed.Window.PartitionBy.IsDefaultOrEmpty)
        {
            var partition = windowed.Window.PartitionBy
                .Select(item => RenderExpression(item, compiler))
                .ToArray();
            parts.Add("PARTITION BY " + string.Join(", ", partition.Select(item => item.Sql)));
            foreach (var item in partition) bindings.AddRange(item.Bindings);
        }

        if (!windowed.Window.OrderBy.IsDefaultOrEmpty)
        {
            var orderParts = new List<string>();
            foreach (var item in windowed.Window.OrderBy)
            {
                var rendered = RenderExpression(item.Expression, compiler);
                var sql = rendered.Sql + (item.Descending ? " DESC" : " ASC");
                sql += item.NullOrdering switch
                {
                    NullOrderingKind.Default => string.Empty,
                    NullOrderingKind.First => " NULLS FIRST",
                    NullOrderingKind.Last => " NULLS LAST",
                    _ => throw new SqlCompilationException(
                        $"Unsupported NULL ordering '{item.NullOrdering}' in window.")
                };
                orderParts.Add(sql);
                bindings.AddRange(rendered.Bindings);
            }
            parts.Add("ORDER BY " + string.Join(", ", orderParts));
        }

        if (windowed.Window.Frame is not null)
            parts.Add(RenderWindowFrame(windowed.Window.Frame));

        return new RenderedExpression(
            $"{expression.Sql} OVER ({string.Join(" ", parts)})",
            bindings.ToImmutable());
    }

    private static string RenderWindowFrame(WindowFrame frame)
    {
        var unit = frame.Unit switch
        {
            WindowFrameUnitKind.Rows => "ROWS",
            WindowFrameUnitKind.Range => "RANGE",
            _ => throw new SqlCompilationException($"Unsupported window frame unit '{frame.Unit}'.")
        };
        var start = RenderWindowBound(frame.Start);
        return frame.End is null
            ? $"{unit} {start}"
            : $"{unit} BETWEEN {start} AND {RenderWindowBound(frame.End)}";
    }

    private static string RenderWindowBound(WindowFrameBoundCore bound) => bound.Kind switch
    {
        WindowFrameBoundKindCore.UnboundedPreceding => "UNBOUNDED PRECEDING",
        WindowFrameBoundKindCore.Preceding when bound.Offset is >= 0 => $"{bound.Offset.Value} PRECEDING",
        WindowFrameBoundKindCore.CurrentRow => "CURRENT ROW",
        WindowFrameBoundKindCore.Following when bound.Offset is >= 0 => $"{bound.Offset.Value} FOLLOWING",
        WindowFrameBoundKindCore.UnboundedFollowing => "UNBOUNDED FOLLOWING",
        _ => throw new SqlCompilationException($"Invalid window frame bound '{bound.Kind}'.")
    };

    private static RenderedExpression RenderCast(CastExpr cast, Compiler compiler)
    {
        if (!SafeCastType.IsMatch(cast.TypeName))
            throw new SqlCompilationException($"Unsafe CAST type '{cast.TypeName}'.");
        var inner = RenderExpression(cast.Expression, compiler);
        return inner with { Sql = $"CAST({inner.Sql} AS {cast.TypeName})" };
    }

    private static RenderedExpression RenderSimpleCase(SimpleCaseExpr @case, Compiler compiler)
    {
        if (@case.Branches.IsDefaultOrEmpty)
            throw new SqlCompilationException("Simple CASE requires at least one WHEN branch.");

        var first = RequireSimpleCaseComparison(@case.Branches[0]);
        var operand = RenderExpression(first.Left, compiler);
        var bindings = ImmutableArray.CreateBuilder<object?>();
        bindings.AddRange(operand.Bindings);
        var parts = new List<string>();

        foreach (var branch in @case.Branches)
        {
            var comparison = RequireSimpleCaseComparison(branch);
            var match = RenderExpression(comparison.Right, compiler);
            var value = RenderExpression(branch.Value, compiler);
            parts.Add($"WHEN {match.Sql} THEN {value.Sql}");
            bindings.AddRange(match.Bindings);
            bindings.AddRange(value.Bindings);
        }

        if (@case.ElseExpression is not null)
        {
            var otherwise = RenderExpression(@case.ElseExpression, compiler);
            parts.Add($"ELSE {otherwise.Sql}");
            bindings.AddRange(otherwise.Bindings);
        }

        return new RenderedExpression(
            $"CASE {operand.Sql} {string.Join(" ", parts)} END",
            bindings.ToImmutable());
    }

    private static BinaryExpr RequireSimpleCaseComparison(CaseBranch branch) =>
        branch.Condition is BinaryExpr { Operator: "=" } comparison
            ? comparison
            : throw new SqlCompilationException(
                "Simple CASE branch lost its canonical equality shape before lowering.");

    private static RenderedExpression RenderCase(CaseExpr @case, Compiler compiler)
    {
        var bindings = ImmutableArray.CreateBuilder<object?>();
        var parts = new List<string>();
        foreach (var branch in @case.Branches)
        {
            var condition = RenderExpression(branch.Condition, compiler);
            var value = RenderExpression(branch.Value, compiler);
            parts.Add($"WHEN {condition.Sql} THEN {value.Sql}");
            bindings.AddRange(condition.Bindings);
            bindings.AddRange(value.Bindings);
        }
        if (@case.ElseExpression is not null)
        {
            var otherwise = RenderExpression(@case.ElseExpression, compiler);
            parts.Add($"ELSE {otherwise.Sql}");
            bindings.AddRange(otherwise.Bindings);
        }
        return new RenderedExpression(
            $"CASE {string.Join(" ", parts)} END",
            bindings.ToImmutable());
    }

    private static RenderedExpression RenderIn(InExpr @in, Compiler compiler)
    {
        if (@in.Items.IsDefaultOrEmpty)
            throw new SqlCompilationException("IN requires at least one item.");
        var value = RenderExpression(@in.Value, compiler);
        var items = @in.Items.Select(item => RenderExpression(item, compiler)).ToArray();
        var op = @in.IsNegated ? "NOT IN" : "IN";
        return new RenderedExpression(
            $"({value.Sql} {op} ({string.Join(", ", items.Select(item => item.Sql))}))",
            value.Bindings.Concat(items.SelectMany(item => item.Bindings)).ToImmutableArray());
    }

    private static RenderedExpression RenderBetween(BetweenExpr between, Compiler compiler)
    {
        var value = RenderExpression(between.Value, compiler);
        var lower = RenderExpression(between.Lower, compiler);
        var upper = RenderExpression(between.Upper, compiler);
        var op = between.IsNegated ? "NOT BETWEEN" : "BETWEEN";
        return new RenderedExpression(
            $"({value.Sql} {op} {lower.Sql} AND {upper.Sql})",
            value.Bindings.Concat(lower.Bindings).Concat(upper.Bindings).ToImmutableArray());
    }

    private static RenderedExpression RenderIsNull(IsNullExpr isNull, Compiler compiler)
    {
        var value = RenderExpression(isNull.Value, compiler);
        return value with { Sql = $"({value.Sql} IS {(isNull.IsNegated ? "NOT " : string.Empty)}NULL)" };
    }

    private static RenderedExpression RenderSubquery(SqlStatement statement, Compiler compiler)
    {
        var result = compiler.Compile(BuildQuery(statement, compiler));
        return new RenderedExpression(
            $"({ToPositionalSql(result.Sql, result.NamedBindings)})",
            OrderedValues(result.NamedBindings));
    }

    private static RenderedExpression RenderExists(ExistsExpr exists, Compiler compiler)
    {
        var subquery = RenderSubquery(exists.Query, compiler);
        return subquery with
        {
            Sql = $"{(exists.IsNegated ? "NOT " : string.Empty)}EXISTS {subquery.Sql}"
        };
    }

    private static RenderedExpression RenderIdentifier(SqlIdentifier identifier, Compiler compiler) =>
        new(
            RenderIdentifierSql(identifier, compiler, allowWildcard: true),
            ImmutableArray<object?>.Empty);

    private static string RenderIdentifierSql(
        SqlIdentifier identifier,
        Compiler compiler,
        bool allowWildcard)
    {
        if (identifier.Parts.IsDefaultOrEmpty)
            throw new SqlCompilationException("SQL identifier has no parts.");

        var rendered = new string[identifier.Parts.Length];
        for (var i = 0; i < identifier.Parts.Length; i++)
        {
            var part = identifier.Parts[i];
            var wildcard = part.Value == "*" && !part.WasQuoted;
            if (wildcard)
            {
                if (!allowWildcard || i != identifier.Parts.Length - 1)
                    throw new SqlCompilationException("SQL wildcard is only valid as the final expression identifier part.");
                rendered[i] = "*";
                continue;
            }

            ValidateIdentifierPart(part, "identifier");
            rendered[i] = compiler.WrapValue(NormalizeIdentifierValue(part, compiler));
        }

        return string.Join('.', rendered);
    }

    private static string RenderNamedTableSource(NamedTableSource source, Compiler compiler)
    {
        var table = RenderIdentifierSql(source.Name, compiler, allowWildcard: false);
        if (source.Alias is null) return table;

        var alias = RenderAlias(source.Alias, compiler);
        return compiler is OracleCompiler
            ? $"{table} {alias}"
            : $"{table} AS {alias}";
    }

    private static string CteName(SqlIdentifier identifier, Compiler compiler)
    {
        if (identifier.Parts.Length != 1)
            throw new SqlCompilationException("CTE name must contain exactly one identifier part.");
        return AliasText(identifier.Parts[0], compiler);
    }

    private static string AliasText(IdentifierPart alias, Compiler compiler)
    {
        ValidateIdentifierPart(alias, "alias");
        return NormalizeIdentifierValue(alias, compiler);
    }

    private static string RenderAlias(IdentifierPart alias, Compiler compiler) =>
        compiler.WrapValue(AliasText(alias, compiler));

    private static void ValidateIdentifierPart(IdentifierPart part, string label)
    {
        if (part.WasQuoted)
        {
            if (part.Value.Length == 0 || part.Value.Any(char.IsControl))
                throw new SqlCompilationException($"Unsafe quoted SQL {label} '{part.Value}'.");
            return;
        }

        if (!Regex.IsMatch(part.Value, @"^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant))
            throw new SqlCompilationException($"Unsafe SQL {label} '{part.Value}'.");
    }

    private static string NormalizeIdentifierValue(IdentifierPart part, Compiler compiler)
    {
        if (part.WasQuoted || part.PreserveSpelling) return part.Value;
        return compiler switch
        {
            PostgresCompiler => part.Value.ToLowerInvariant(),
            OracleCompiler or FirebirdCompiler => part.Value.ToUpperInvariant(),
            _ => part.Value
        };
    }

    private static void RequireArguments(FunctionCallExpr function, int count)
    {
        if (function.Arguments.Length != count)
            throw new SqlCompilationException(
                $"Canonical function '{IdentifierText(function.Name)}' requires {count} argument(s).");
    }

    private static string LiteralKeyword(SqlExpr expression, string label)
    {
        if (expression is not LiteralExpr { Value: string value })
            throw new SqlCompilationException($"{label} must be a canonical literal keyword.");
        var normalized = value.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(normalized, "^[A-Z_]+$", RegexOptions.CultureInvariant))
            throw new SqlCompilationException($"Unsafe {label} '{value}'.");
        return normalized;
    }

    private static string SqlStringLiteral(SqlExpr expression, string label)
    {
        if (expression is not LiteralExpr { Value: string value })
            throw new SqlCompilationException($"{label} must be a string literal.");
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static IReadOnlyList<string> JsonPathSegments(SqlExpr expression)
    {
        if (expression is not LiteralExpr { Value: string path })
            throw new SqlCompilationException("JSON path must be a string literal for structured PostgreSQL lowering.");
        var trimmed = path.Trim();
        if (!trimmed.StartsWith('$'))
            throw new SqlCompilationException($"Unsupported JSON path '{path}'.");
        var remainder = trimmed[1..].TrimStart('.');
        if (string.IsNullOrEmpty(remainder)) return [];
        var segments = remainder.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => !Regex.IsMatch(segment, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)))
            throw new SqlCompilationException($"Unsupported structured JSON path '{path}'.");
        return segments;
    }

    private static object? NormalizeBindingValue(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt32(out var i32) => i32,
                JsonValueKind.Number when element.TryGetInt64(out var i64) => i64,
                JsonValueKind.Number when element.TryGetDecimal(out var dec) => dec,
                JsonValueKind.Number => element.GetDouble(),
                _ => throw new SqlCompilationException(
                    $"JSON value kind {element.ValueKind} cannot be bound as a scalar SQL parameter.")
            };
        }

        return value switch
        {
            SqlDateValue date => date.Value.ToDateTime(TimeOnly.MinValue),
            SqlTimeValue time => time.Value.ToTimeSpan(),
            SqlLocalDateTimeValue local => DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified),
            SqlOffsetDateTimeValue offset => offset.Value,
            DateTime dateTime when dateTime.Kind != DateTimeKind.Unspecified =>
                DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),
            _ => value
        };
    }

    private static RenderedExpression Combine(
        string sql,
        RenderedExpression left,
        RenderedExpression right) =>
        new(sql, left.Bindings.Concat(right.Bindings).ToImmutableArray());

    internal static Compiler CreateCompiler(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.Sqlite => new CoreSqliteCompiler(),
        SqlAgentToolType.Postgres => new CorePostgresCompiler(),
        SqlAgentToolType.MySQL => new CoreMySqlCompiler(),
        SqlAgentToolType.MsSqlServer => new CoreSqlServerCompiler { UseLegacyPagination = true },
        SqlAgentToolType.Oracle => new CoreOracleCompiler(),
        SqlAgentToolType.Firebird => new CoreFirebirdCompiler(),
        _ => throw new SqlCompilationException($"Unsupported target provider '{provider}'.")
    };

    private static int ParameterOrdinal(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    private static ImmutableArray<object?> OrderedValues(IReadOnlyDictionary<string, object> bindings) =>
        bindings.OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => NormalizeBindingValue(pair.Value))
            .ToImmutableArray();

    private static string ToPositionalSql(
        string sql,
        IReadOnlyDictionary<string, object> bindings)
    {
        foreach (var pair in bindings.OrderByDescending(pair => ParameterOrdinal(pair.Key)))
            sql = sql.Replace(pair.Key, "?", StringComparison.Ordinal);
        return sql;
    }

    private static string ComputePlanFingerprint(
        string sql,
        ImmutableArray<SqlParameterValue> parameters,
        ExecutableSqlPlan plan)
    {
        var command = new CompiledSqlCommand(
            sql,
            parameters,
            SqlStatementKind.Select,
            string.Empty,
            plan.TargetProvider);
        return Core.Execution.DmlFingerprintService.ComputePlanFingerprint(
            command,
            plan.PolicyVersion);
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private sealed record RenderedExpression(
        string Sql,
        ImmutableArray<object?> Bindings);
}
