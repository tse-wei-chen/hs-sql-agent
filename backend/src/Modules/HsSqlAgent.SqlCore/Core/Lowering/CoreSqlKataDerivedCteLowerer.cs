using System.Collections.Immutable;
using System.Text.Json;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Execution;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using SqlKata;
using SqlKata.Compilers;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Query lowerer for nested query-graph positions that own statement-root CTEs. SqlKata normally
/// compiles derived QueryFromClause and set Combine branches through CompileSelectQuery, which
/// omits nested WITH components, and CteFinder recursively hoists QueryFromClause CTE definitions.
/// This adapter fully compiles scope-owning fragments, converts compiler-owned parameter names back
/// to positional bindings, and reattaches them as raw Core-generated fragments. CTE-free nested
/// queries keep the ordinary structured SqlKata path.
/// </summary>
internal sealed class CoreSqlKataDerivedCteLowerer(SqlAgentToolType provider) : IProviderLowerer
{
    private const string SetBranchAlias = "_set_branch";

    public SqlAgentToolType Provider { get; } = provider;

    public static bool CanLower(SqlStatement statement) =>
        ContainsNestedCteFragment(statement);

    internal static void RewriteQueryGraph(Query query, Compiler compiler) =>
        RewriteNestedCteSources(query, compiler);

    public CompiledSqlCommand Lower(ExecutableSqlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.TargetProvider != Provider)
        {
            throw new SqlCompilationException(
                $"Plan targets {plan.TargetProvider}, but this lowerer targets {Provider}.");
        }

        var compiler = SqlKataProviderLowerer.CreateCompiler(Provider);
        var query = SqlKataProviderLowerer.BuildQuery(plan.Statement, compiler);
        RewriteQueryGraph(query, compiler);
        var result = compiler.Compile(query);
        var parameters = result.NamedBindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => new SqlParameterValue(pair.Key, NormalizeBindingValue(pair.Value)))
            .ToImmutableArray();

        var command = new CompiledSqlCommand(
            result.Sql,
            parameters,
            SqlStatementKind.Select,
            string.Empty,
            Provider);
        return command with
        {
            PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(command, plan.PolicyVersion)
        };
    }

    private static bool ContainsNestedCteFragment(SqlStatement statement) => statement switch
    {
        SelectStatement select =>
            SourceContainsNestedCte(select.From)
            || select.Joins.Any(join => SourceContainsNestedCte(join.Source))
            || select.Ctes.Any(cte =>
                HasRootCtes(cte.Query) || ContainsNestedCteFragment(cte.Query)),
        QueryStatement query =>
            ContainsNestedCteFragment(query.Head)
            || query.SetOperations.Any(operation =>
                HasRootCtes(operation.Query)
                || ContainsNestedCteFragment(operation.Query)),
        _ => false
    };

    private static bool SourceContainsNestedCte(TableSource? source) => source switch
    {
        DerivedTableSource derived =>
            HasRootCtes(derived.Query) || ContainsNestedCteFragment(derived.Query),
        _ => false
    };

    private static bool HasRootCtes(SqlStatement statement) => statement switch
    {
        SelectStatement select => !select.Ctes.IsDefaultOrEmpty,
        QueryStatement query => !query.Head.Ctes.IsDefaultOrEmpty,
        _ => false
    };

    private static void RewriteNestedCteSources(Query query, Compiler compiler)
    {
        RewriteCteClauses(query.Clauses, compiler);
        RewriteFromClause(query.Clauses, compiler);

        foreach (var join in query.GetComponents<BaseJoin>("join").ToArray())
            RewriteFromClause(join.Join.Clauses, compiler);

        foreach (var combine in query.GetComponents<Combine>("combine").ToArray())
        {
            // SqlKata's Combine.Clone keeps the original Query reference. Detach it before any
            // rewrite so compiler-time normalization cannot mutate a caller-owned query graph.
            combine.Query = combine.Query.Clone();
            RewriteNestedCteSources(combine.Query, compiler);
            if (!combine.Query.HasComponent("cte"))
                continue;

            var rendered = CompileFragment(combine.Query, compiler);
            combine.Query = CreateFragmentWrapper(rendered, SetBranchAlias, compiler);
        }
    }

    private static void RewriteCteClauses(List<AbstractClause> clauses, Compiler compiler)
    {
        for (var index = 0; index < clauses.Count; index++)
        {
            if (clauses[index] is not QueryFromClause cte || cte.Component != "cte")
                continue;

            var body = cte.Query.Clone();
            RewriteNestedCteSources(body, compiler);
            if (!body.HasComponent("cte"))
            {
                cte.Query = body;
                continue;
            }

            if (string.IsNullOrWhiteSpace(cte.Alias))
                throw new SqlCompilationException("A CTE definition requires an alias before lowering.");

            var rendered = CompileFragment(body, compiler);
            clauses[index] = new RawFromClause
            {
                Component = cte.Component,
                Engine = cte.Engine,
                Alias = cte.Alias,
                Expression = rendered.Sql,
                Bindings = rendered.Bindings.ToArray()
            };
        }
    }

    private static void RewriteFromClause(List<AbstractClause> clauses, Compiler compiler)
    {
        for (var index = 0; index < clauses.Count; index++)
        {
            if (clauses[index] is not QueryFromClause from || from.Component != "from")
                continue;

            var body = from.Query.Clone();
            RewriteNestedCteSources(body, compiler);
            if (!body.HasComponent("cte"))
            {
                from.Query = body;
                continue;
            }

            if (string.IsNullOrWhiteSpace(from.Alias))
                throw new SqlCompilationException("A derived table with a CTE requires an alias.");

            var rendered = CompileFragment(body, compiler);
            clauses[index] = CreateRawFromClause(
                rendered,
                from.Alias,
                from.Component,
                from.Engine,
                compiler);
        }
    }

    private static Query CreateFragmentWrapper(
        RenderedFragment rendered,
        string alias,
        Compiler compiler) =>
        new Query()
            .FromRaw(
                RenderDerivedExpression(rendered.Sql, alias, compiler),
                rendered.Bindings.ToArray())
            .Select("*");

    private static RawFromClause CreateRawFromClause(
        RenderedFragment rendered,
        string alias,
        string component,
        string? engine,
        Compiler compiler) =>
        new()
        {
            Component = component,
            Engine = engine,
            Alias = alias,
            Expression = RenderDerivedExpression(rendered.Sql, alias, compiler),
            Bindings = rendered.Bindings.ToArray()
        };

    private static string RenderDerivedExpression(
        string sql,
        string alias,
        Compiler compiler)
    {
        var renderedAlias = compiler.WrapValue(alias);
        return compiler is OracleCompiler
            ? $"({sql}) {renderedAlias}"
            : $"({sql}) AS {renderedAlias}";
    }

    private static RenderedFragment CompileFragment(Query query, Compiler compiler)
    {
        var result = compiler.Compile(query.Clone());
        return new RenderedFragment(
            ToPositionalSql(result.Sql, result.NamedBindings),
            result.NamedBindings
                .OrderBy(pair => ParameterOrdinal(pair.Key))
                .Select(pair => NormalizeBindingValue(pair.Value))
                .ToImmutableArray());
    }

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

    private sealed record RenderedFragment(
        string Sql,
        ImmutableArray<object?> Bindings);
}
