using System.Collections.Immutable;
using System.Text.RegularExpressions;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlKata;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Lowers the provider-neutral Core AST into the existing SqlKata backend. Statement structure is
/// represented by SqlKata Query nodes. Expression raw fragments are generated only from closed
/// Core node/operator types; identifiers are quoted by the target compiler and values are bindings.
/// Raw user SQL never crosses this boundary.
/// </summary>
public sealed class SqlKataProviderLowerer(SqlAgentToolType provider) : IProviderLowerer
{
    private static readonly Regex SafeCastType = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*(?:\([0-9]+(?:,[0-9]+)?\))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public SqlAgentToolType Provider { get; } = provider;

    public CompiledSqlCommand Lower(ExecutableSqlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.TargetProvider != Provider)
            throw new SqlCompilationException(
                $"Plan targets {plan.TargetProvider}, but this lowerer targets {Provider}.");

        var compiler = CreateCompiler(Provider);
        var query = LowerStatement(plan.Statement, compiler);
        var result = compiler.Compile(query);
        var parameters = result.NamedBindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => new SqlParameterValue(pair.Key, pair.Value))
            .ToImmutableArray();

        return new CompiledSqlCommand(
            result.Sql,
            parameters,
            SqlStatementKind.Select,
            ComputePlanFingerprint(result.Sql, parameters, plan),
            Provider);
    }

    private static Query LowerStatement(SqlStatement statement, Compiler compiler) => statement switch
    {
        SelectStatement select => LowerSelect(select, compiler),
        QueryStatement query => LowerQuery(query, compiler),
        _ => throw new SqlCompilationException(
            $"Unsupported statement during SqlKata lowering: {statement.GetType().Name}")
    };

    private static Query LowerQuery(QueryStatement statement, Compiler compiler)
    {
        var query = LowerSelect(statement.Head, compiler, includeTail: false);
        foreach (var operation in statement.SetOperations)
        {
            var branch = LowerStatement(operation.Query, compiler);
            query = operation.Kind switch
            {
                SetOperationKind.Union => query.Union(branch),
                SetOperationKind.UnionAll => query.UnionAll(branch),
                SetOperationKind.Intersect => query.Intersect(branch),
                SetOperationKind.Except => query.Except(branch),
                _ => throw new SqlCompilationException(
                    $"Unsupported set operation '{operation.Kind}'.")
            };
        }

        ApplyOrderBy(query, statement.OrderBy, compiler);
        if (statement.Limit is > 0) query.Limit(statement.Limit.Value);
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
            query.With(IdentifierText(cte.Name), LowerStatement(cte.Query, compiler));
        }

        if (statement.From is not null)
            ApplyFrom(query, statement.From, compiler);

        if (statement.Distinct) query.Distinct();

        foreach (var item in statement.Select)
        {
            var rendered = RenderExpression(item.Expression, compiler);
            query.Select(new RawColumn
            {
                Expression = rendered.Sql,
                Bindings = rendered.Bindings.ToArray(),
                Alias = item.Alias
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
            ApplyOrderBy(query, statement.OrderBy, compiler);
            if (statement.Limit is > 0) query.Limit(statement.Limit.Value);
            if (statement.Offset is > 0) query.Offset(statement.Offset.Value);
        }

        return query;
    }

    private static void ApplyFrom(Query query, TableSource source, Compiler compiler)
    {
        switch (source)
        {
            case NamedTableSource named:
            {
                var name = IdentifierText(named.Name);
                query.From(string.IsNullOrWhiteSpace(named.Alias)
                    ? name
                    : $"{name} AS {named.Alias}");
                return;
            }
            case DerivedTableSource derived:
                query.From(LowerStatement(derived.Query, compiler), derived.Alias);
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
                    query.CrossJoin(string.IsNullOrWhiteSpace(named.Alias)
                        ? IdentifierText(named.Name)
                        : $"{IdentifierText(named.Name)} AS {named.Alias}");
                    return;
                case DerivedTableSource derived:
                    query.Join(
                        LowerStatement(derived.Query, compiler).As(derived.Alias),
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
            {
                var table = string.IsNullOrWhiteSpace(named.Alias)
                    ? IdentifierText(named.Name)
                    : $"{IdentifierText(named.Name)} AS {named.Alias}";
                query.Join(
                    table,
                    j => j.WhereRaw(predicate.Sql, predicate.Bindings.ToArray()),
                    type);
                return;
            }
            case DerivedTableSource derived:
                query.Join(
                    LowerStatement(derived.Query, compiler).As(derived.Alias),
                    j => j.WhereRaw(predicate.Sql, predicate.Bindings.ToArray()),
                    type);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported JOIN source '{join.Source.GetType().Name}'.");
        }
    }

    private static void ApplyOrderBy(
        Query query,
        IEnumerable<OrderByItem> items,
        Compiler compiler)
    {
        foreach (var item in items)
        {
            var rendered = RenderExpression(item.Expression, compiler);
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

    private static RenderedExpression RenderExpression(SqlExpr expression, Compiler compiler)
    {
        return expression switch
        {
            BoundColumnExpr column => RenderIdentifier(column.Name, compiler),
            ColumnExpr column => RenderIdentifier(column.Name, compiler),
            LiteralExpr literal => new RenderedExpression("?", [literal.Value]),
            IntervalExpr => throw new SqlCompilationException(
                "INTERVAL lowering is not yet implemented in the Core SqlKata backend."),
            UnaryExpr unary => RenderUnary(unary, compiler),
            BinaryExpr binary => RenderBinary(binary, compiler),
            FunctionCallExpr function => RenderFunction(function, compiler),
            CastExpr cast => RenderCast(cast, compiler),
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
        return Combine($"({left.Sql} {op} {right.Sql})", left, right);
    }

    private static RenderedExpression RenderFunction(FunctionCallExpr function, Compiler compiler)
    {
        var name = IdentifierText(function.Name);
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new SqlCompilationException($"Unsafe function identifier '{name}'.");

        var args = function.Arguments.Select(arg => RenderExpression(arg, compiler)).ToArray();
        var sql = string.Join(", ", args.Select(arg => arg.Sql));
        if (function.IsDistinct) sql = "DISTINCT " + sql;
        return new RenderedExpression(
            $"{name}({sql})",
            args.SelectMany(arg => arg.Bindings).ToImmutableArray());
    }

    private static RenderedExpression RenderCast(CastExpr cast, Compiler compiler)
    {
        if (!SafeCastType.IsMatch(cast.TypeName))
            throw new SqlCompilationException($"Unsafe CAST type '{cast.TypeName}'.");
        var inner = RenderExpression(cast.Expression, compiler);
        return inner with { Sql = $"CAST({inner.Sql} AS {cast.TypeName})" };
    }

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
        var result = compiler.Compile(LowerStatement(statement, compiler));
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

    private static RenderedExpression RenderIdentifier(SqlIdentifier identifier, Compiler compiler)
    {
        if (identifier.Parts.IsDefaultOrEmpty)
            throw new SqlCompilationException("SQL identifier has no parts.");
        foreach (var part in identifier.Parts)
        {
            if (part.Value != "*" && !Regex.IsMatch(
                    part.Value,
                    @"^[A-Za-z_][A-Za-z0-9_$]*$",
                    RegexOptions.CultureInvariant))
            {
                throw new SqlCompilationException($"Unsafe SQL identifier part '{part.Value}'.");
            }
        }
        return new RenderedExpression(
            compiler.Wrap(IdentifierText(identifier)),
            ImmutableArray<object?>.Empty);
    }

    private static RenderedExpression Combine(
        string sql,
        RenderedExpression left,
        RenderedExpression right) =>
        new(sql, left.Bindings.Concat(right.Bindings).ToImmutableArray());

    private static Compiler CreateCompiler(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.Sqlite => new SqliteCompiler(),
        SqlAgentToolType.Postgres => new PostgresCompiler(),
        SqlAgentToolType.MySQL => new MySqlCompiler(),
        SqlAgentToolType.MsSqlServer => new SqlServerCompiler(),
        SqlAgentToolType.Oracle => new OracleCompiler(),
        SqlAgentToolType.Firebird => new FirebirdCompiler(),
        _ => throw new SqlCompilationException($"Unsupported target provider '{provider}'.")
    };

    private static int ParameterOrdinal(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    private static ImmutableArray<object?> OrderedValues(IReadOnlyDictionary<string, object> bindings) =>
        bindings.OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => (object?)pair.Value)
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
