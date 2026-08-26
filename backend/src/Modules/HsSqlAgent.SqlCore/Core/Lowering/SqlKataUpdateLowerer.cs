using System.Collections.Immutable;
using System.Text;
using HsSqlAgent.SqlCore.Core.Execution;
using SqlKata.Compilers;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Core-owned UPDATE lowering. SqlKata's stock AsUpdate path treats every assignment value as a
/// scalar binding, so it cannot preserve structured expressions such as column arithmetic, CASE,
/// CAST, or provider-specific canonical functions. This lowerer renders each already-validated
/// Core expression through the query expression backend, then lets the Core-owned SqlKata compiler
/// perform final positional-to-named parameter preparation for the complete UPDATE statement.
/// </summary>
public sealed class SqlKataUpdateLowerer(SqlAgentToolType provider)
{
    private const string ProjectionAlias = "__core_update_value";
    private const string PredicateProbe = "__core_predicate_probe";

    public CompiledSqlCommand Lower(ExecutableSqlPlan plan, UpdateStatement update)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(update);
        if (plan.TargetProvider != provider)
        {
            throw new SqlCompilationException(
                $"Plan targets {plan.TargetProvider}, but this UPDATE lowerer targets {provider}.");
        }
        if (update.Assignments.IsDefaultOrEmpty)
            throw new SqlCompilationException("UPDATE requires at least one assignment.");

        var compiler = SqlKataProviderLowerer.CreateCompiler(provider);
        var bindings = ImmutableArray.CreateBuilder<object?>();
        var assignmentSql = new List<string>(update.Assignments.Length);

        foreach (var assignment in update.Assignments)
        {
            var column = CoreIdentifierSqlRenderer.Render(
                assignment.Column,
                compiler,
                allowWildcard: false);
            var value = RenderProjectionExpression(assignment.Value, compiler);
            assignmentSql.Add($"{column} = {value.Sql}");
            bindings.AddRange(value.Bindings);
        }

        var sql = new StringBuilder()
            .Append("UPDATE ")
            .Append(CoreIdentifierSqlRenderer.Render(
                update.Target.Name,
                compiler,
                allowWildcard: false))
            .Append(" SET ")
            .Append(string.Join(", ", assignmentSql));

        if (update.Predicate is not null)
        {
            var predicate = RenderPredicate(update.Predicate, compiler);
            sql.Append(" WHERE ").Append(predicate.Sql);
            bindings.AddRange(predicate.Bindings);
        }

        var result = CoreSqlKataRawCompiler.Prepare(
            compiler,
            sql.ToString(),
            bindings.ToImmutable());
        var parameters = result.NamedBindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => new SqlParameterValue(pair.Key, pair.Value))
            .ToImmutableArray();
        var command = new CompiledSqlCommand(
            result.Sql,
            parameters,
            SqlStatementKind.Update,
            string.Empty,
            provider);
        return command with
        {
            PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(
                command,
                plan.PolicyVersion)
        };
    }

    private static RenderedExpression RenderProjectionExpression(
        SqlExpr expression,
        Compiler compiler)
    {
        var select = EmptySelect(
            new SelectItem(
                expression,
                new IdentifierPart(
                    ProjectionAlias,
                    WasQuoted: false,
                    SourceSpan.Unknown,
                    PreserveSpelling: true),
                expression.Span));
        var result = compiler.Compile(SqlKataProviderLowerer.BuildQuery(select, compiler));
        var marker = " AS " + compiler.WrapValue(ProjectionAlias);
        var markerIndex = result.Sql.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        const string prefix = "SELECT ";
        if (!result.Sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || markerIndex < prefix.Length)
        {
            throw new SqlCompilationException(
                "Core UPDATE expression rendering could not isolate the compiled SELECT projection.");
        }

        var fragment = result.Sql[prefix.Length..markerIndex].Trim();
        return new RenderedExpression(
            ToPositionalSql(fragment, result.NamedBindings),
            OrderedValues(result.NamedBindings));
    }

    private static RenderedExpression RenderPredicate(SqlExpr expression, Compiler compiler)
    {
        var probe = new ColumnExpr(
            SqlIdentifier.Unquoted(PredicateProbe, SourceSpan.Unknown),
            SourceSpan.Unknown);
        var select = EmptySelect(new SelectItem(probe, Alias: null, Span: SourceSpan.Unknown)) with
        {
            Where = expression
        };
        var result = compiler.Compile(SqlKataProviderLowerer.BuildQuery(select, compiler));
        const string marker = " WHERE ";
        var markerIndex = result.Sql.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 || markerIndex + marker.Length >= result.Sql.Length)
        {
            throw new SqlCompilationException(
                "Core UPDATE predicate rendering could not isolate the compiled WHERE expression.");
        }

        var fragment = result.Sql[(markerIndex + marker.Length)..].Trim();
        return new RenderedExpression(
            ToPositionalSql(fragment, result.NamedBindings),
            OrderedValues(result.NamedBindings));
    }

    private static SelectStatement EmptySelect(SelectItem item) =>
        new(
            Ctes: ImmutableArray<CteDefinition>.Empty,
            Distinct: false,
            Select: ImmutableArray.Create(item),
            From: null,
            Joins: ImmutableArray<JoinSource>.Empty,
            Where: null,
            GroupBy: ImmutableArray<SqlExpr>.Empty,
            Having: null,
            OrderBy: ImmutableArray<OrderByItem>.Empty,
            Limit: null,
            Offset: null,
            Span: item.Span);

    private static string ToPositionalSql(
        string sql,
        IReadOnlyDictionary<string, object> bindings)
    {
        foreach (var pair in bindings.OrderByDescending(pair => ParameterOrdinal(pair.Key)))
            sql = sql.Replace(pair.Key, "?", StringComparison.Ordinal);
        return sql;
    }

    private static ImmutableArray<object?> OrderedValues(
        IReadOnlyDictionary<string, object> bindings) =>
        bindings
            .OrderBy(pair => ParameterOrdinal(pair.Key))
            .Select(pair => (object?)pair.Value)
            .ToImmutableArray();

    private static int ParameterOrdinal(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    private sealed record RenderedExpression(
        string Sql,
        ImmutableArray<object?> Bindings);
}
