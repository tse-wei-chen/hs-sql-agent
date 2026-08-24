using System.Collections.Immutable;
using System.Text.Json;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlKata;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Query lowerer for provider/query-graph positions where a derived table owns a statement-root
/// CTE. SqlKata normally compiles a QueryFromClause through CompileSelectQuery and omits that
/// nested query's WITH components. This adapter compiles only those derived query nodes through a
/// full target compiler invocation, converts compiler-owned parameter names back to positional
/// bindings, and replaces the nested source with a RawFromClause. CTE-free derived tables keep the
/// ordinary structured SqlKata path.
/// </summary>
internal sealed class CoreSqlKataDerivedCteLowerer(SqlAgentToolType provider) : IProviderLowerer
{
    public SqlAgentToolType Provider { get; } = provider;

    public static bool CanLower(SqlStatement statement) =>
        ContainsDerivedCte(statement);

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
        RewriteDerivedCteSources(query, compiler);
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

    private static bool ContainsDerivedCte(SqlStatement statement) => statement switch
    {
        SelectStatement select =>
            SourceContainsDerivedCte(select.From)
            || select.Joins.Any(join => SourceContainsDerivedCte(join.Source))
            || select.Ctes.Any(cte => ContainsDerivedCte(cte.Query)),
        QueryStatement query =>
            ContainsDerivedCte(query.Head)
            || query.SetOperations.Any(operation => ContainsDerivedCte(operation.Query)),
        _ => false
    };

    private static bool SourceContainsDerivedCte(TableSource? source) => source switch
    {
        DerivedTableSource derived =>
            HasRootCtes(derived.Query) || ContainsDerivedCte(derived.Query),
        _ => false
    };

    private static bool HasRootCtes(SqlStatement statement) => statement switch
    {
        SelectStatement select => !select.Ctes.IsDefaultOrEmpty,
        QueryStatement query => !query.Head.Ctes.IsDefaultOrEmpty,
        _ => false
    };

    private static void RewriteDerivedCteSources(Query query, Compiler compiler)
    {
        foreach (var cte in query.GetComponents<QueryFromClause>("cte").ToArray())
            RewriteDerivedCteSources(cte.Query, compiler);

        RewriteFromClause(query.Clauses, compiler);

        foreach (var join in query.GetComponents<BaseJoin>("join").ToArray())
            RewriteFromClause(join.Join.Clauses, compiler);

        foreach (var combine in query.GetComponents<Combine>("combine").ToArray())
            RewriteDerivedCteSources(combine.Query, compiler);
    }

    private static void RewriteFromClause(List<AbstractClause> clauses, Compiler compiler)
    {
        for (var index = 0; index < clauses.Count; index++)
        {
            if (clauses[index] is not QueryFromClause from || from.Component != "from")
                continue;

            RewriteDerivedCteSources(from.Query, compiler);
            if (!from.Query.HasComponent("cte"))
                continue;

            if (string.IsNullOrWhiteSpace(from.Alias))
                throw new SqlCompilationException("A derived table with a CTE requires an alias.");

            var rendered = CompileFragment(from.Query, compiler);
            var alias = compiler.WrapValue(from.Alias);
            var expression = compiler is OracleCompiler
                ? $"({rendered.Sql}) {alias}"
                : $"({rendered.Sql}) AS {alias}";
            clauses[index] = new RawFromClause
            {
                Component = from.Component,
                Engine = from.Engine,
                Alias = from.Alias,
                Expression = expression,
                Bindings = rendered.Bindings.ToArray()
            };
        }
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
