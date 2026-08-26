using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Execution;
using SqlKata.Compilers;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Lowers INSERT ... SELECT sources whose statement-root query owns CTE definitions. SqlKata's
/// normal insert-query path compiles the source through CompileSelectQuery and drops root WITH
/// components. This Core-owned path renders each already-validated CTE/query fragment through the
/// target compiler, converts only compiler-generated placeholders back to positional bindings, and
/// assembles the provider-specific CTE placement without inlining runtime values.
/// </summary>
internal static class CoreInsertSelectCteLowerer
{
    public static bool CanLower(InsertStatement insert) =>
        insert.Source is InsertQuerySource querySource
        && !RootCtes(querySource.Query).IsDefaultOrEmpty;

    public static CompiledSqlCommand Lower(
        ExecutableSqlPlan plan,
        InsertStatement insert)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(insert);
        if (insert.Source is not InsertQuerySource querySource)
            throw new SqlCompilationException("CTE INSERT lowerer requires an INSERT ... SELECT source.");

        var ctes = RootCtes(querySource.Query);
        if (ctes.IsDefaultOrEmpty)
            throw new SqlCompilationException("CTE INSERT lowerer requires a statement-root CTE.");
        if (insert.Columns.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT requires at least one target column.");

        var compiler = SqlKataProviderLowerer.CreateCompiler(plan.TargetProvider);
        var bindings = ImmutableArray.CreateBuilder<object?>();
        var cteSql = new List<string>(ctes.Length);
        foreach (var cte in ctes)
        {
            if (!cte.ColumnAliases.IsDefaultOrEmpty)
            {
                throw new SqlCompilationException(
                    "CTE column aliases must be canonicalized to projection aliases before INSERT lowering.");
            }

            var rendered = CompileFragment(cte.Query, compiler);
            bindings.AddRange(rendered.Bindings);
            var name = CoreIdentifierSqlRenderer.Render(cte.Name, compiler, allowWildcard: false);
            cteSql.Add($"{name} AS ({rendered.Sql})");
        }

        var source = CompileFragment(RemoveRootCtes(querySource.Query), compiler);
        bindings.AddRange(source.Bindings);

        var table = CoreIdentifierSqlRenderer.Render(insert.Target.Name, compiler, allowWildcard: false);
        var columns = string.Join(", ", insert.Columns.Select(column =>
            CoreIdentifierSqlRenderer.Render(column, compiler, allowWildcard: false)));
        var insertPrefix = $"INSERT INTO {table} ({columns})";
        var withClause = "WITH " + string.Join(", ", cteSql);
        var rawSql = plan.TargetProvider switch
        {
            SqlAgentToolType.Postgres or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite =>
                $"{withClause} {insertPrefix} {source.Sql}",
            SqlAgentToolType.MySQL or SqlAgentToolType.Oracle or SqlAgentToolType.Firebird =>
                $"{insertPrefix} {withClause} {source.Sql}",
            _ => throw new SqlCompilationException(
                $"INSERT ... SELECT CTE placement is not declared for provider {plan.TargetProvider}.")
        };

        var result = CoreSqlKataRawCompiler.Prepare(compiler, rawSql, bindings.ToImmutable());
        var parameters = result.NamedBindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => new SqlParameterValue(pair.Key, pair.Value))
            .ToImmutableArray();
        var command = new CompiledSqlCommand(
            result.Sql,
            parameters,
            SqlStatementKind.Insert,
            string.Empty,
            plan.TargetProvider);
        return command with
        {
            PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(command, plan.PolicyVersion)
        };
    }

    private static RenderedFragment CompileFragment(SqlStatement statement, Compiler compiler)
    {
        var result = compiler.Compile(SqlKataProviderLowerer.BuildQuery(statement, compiler));
        return new RenderedFragment(
            ToPositionalSql(result.Sql, result.NamedBindings),
            result.NamedBindings
                .OrderBy(pair => ParameterOrdinal(pair.Key))
                .Select(pair => (object?)pair.Value)
                .ToImmutableArray());
    }

    private static ImmutableArray<CteDefinition> RootCtes(SqlStatement statement) => statement switch
    {
        SelectStatement select => select.Ctes,
        QueryStatement query => query.Head.Ctes,
        _ => ImmutableArray<CteDefinition>.Empty
    };

    private static SqlStatement RemoveRootCtes(SqlStatement statement) => statement switch
    {
        SelectStatement select => select with { Ctes = ImmutableArray<CteDefinition>.Empty },
        QueryStatement query => query with
        {
            Head = query.Head with { Ctes = ImmutableArray<CteDefinition>.Empty }
        },
        _ => throw new SqlCompilationException(
            $"INSERT ... SELECT source '{statement.GetType().Name}' is not a query statement.")
    };

    private static string ToPositionalSql(
        string sql,
        IReadOnlyDictionary<string, object> bindings)
    {
        foreach (var pair in bindings.OrderByDescending(pair => ParameterOrdinal(pair.Key)))
            sql = sql.Replace(pair.Key, "?", StringComparison.Ordinal);
        return sql;
    }

    private static int ParameterOrdinal(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var ordinal) ? ordinal : int.MaxValue;
    }

    private sealed record RenderedFragment(
        string Sql,
        ImmutableArray<object?> Bindings);
}
