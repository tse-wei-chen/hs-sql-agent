using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Execution;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Adds DML result-row clauses after provider-specific mutation lowering. Portable target-column and
/// wildcard items use the existing cross-provider RETURNING contract. Richer expression items are a
/// separate capability and currently lower only for PostgreSQL when rendering introduces no new
/// runtime bindings after native parameter finalization.
/// </summary>
internal static class CoreDmlReturningSqlRewriter
{
    public static CompiledSqlCommand Apply(
        CompiledSqlCommand command,
        SqlStatement statement,
        SqlProviderCapabilityProfile? targetProfile,
        string policyVersion)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);

        var returning = ReturningItems(statement);
        if (returning.IsDefaultOrEmpty)
            return command;

        var capabilityError = SqlDmlReturningCapabilityRules.TargetValidationError(
            command.TargetProvider,
            targetProfile);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

        var projection = string.Join(", ", returning.Select(item =>
            RenderProjectionItem(item, command.TargetProvider)));
        var rewritten = command with
        {
            Sql = command.Sql.TrimEnd().TrimEnd(';') + " RETURNING " + projection,
            ReturnsRows = true,
            PlanFingerprint = string.Empty
        };
        return rewritten with
        {
            PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(rewritten, policyVersion)
        };
    }

    private static ImmutableArray<DmlReturningItem> ReturningItems(SqlStatement statement) => statement switch
    {
        InsertStatement insert => insert.Returning,
        UpdateStatement update => update.Returning,
        DeleteStatement delete => delete.Returning,
        _ => ImmutableArray<DmlReturningItem>.Empty
    };

    private static string RenderProjectionItem(
        DmlReturningItem item,
        SqlAgentToolType provider) => item switch
    {
        DmlReturningColumnItem column => CoreIdentifierSqlRenderer.Render(
            column.Identifier,
            provider,
            allowWildcard: false),
        DmlReturningWildcardItem => "*",
        DmlReturningExpressionItem expression => RenderExpressionItem(expression, provider),
        _ => throw new InvalidOperationException(
            $"Unsupported DML returning projection item {item.GetType().Name}.")
    };

    private static string RenderExpressionItem(
        DmlReturningExpressionItem item,
        SqlAgentToolType provider)
    {
        var targetError = SqlDmlReturningExpressionCapabilityRules.TargetValidationError(provider);
        if (targetError is not null)
            throw new SqlCompilationException(targetError);

        SqlDmlReturningExpressionCapabilityRules.ValidateExpression(item);
        var fragment = NativeSqlExpressionRenderer.Render(
            item.Expression,
            provider,
            static _ => throw new SqlCompilationException(
                "DML RETURNING expression subqueries are not represented by the current PostgreSQL target-row expression contract."),
            dmlContext: true);

        if (!fragment.Bindings.IsDefaultOrEmpty)
        {
            throw new SqlCompilationException(
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' cannot append runtime bindings after native parameter finalization. Literal-bearing RETURNING expressions remain fail-closed until RETURNING lowering moves before parameter finalization.");
        }

        if (item.Alias is null)
            return fragment.Sql;

        return fragment.Sql + " AS " + CoreIdentifierSqlRenderer.RenderAlias(item.Alias, provider);
    }
}
