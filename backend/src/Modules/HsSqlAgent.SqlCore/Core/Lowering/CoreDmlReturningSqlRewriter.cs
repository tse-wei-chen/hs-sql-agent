using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Execution;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Finalizes the DML result-row contract after native lowering. Ordinary RETURNING clauses are now
/// rendered inside the native DML fragment before parameter finalization; INSERT conflict clauses
/// still use the historical post-conflict RETURNING path until conflict lowering moves native too.
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

        var alreadyRendered = command.Sql.Contains(
            " RETURNING ",
            StringComparison.OrdinalIgnoreCase);
        if (alreadyRendered)
            return MarkReturnsRows(command, policyVersion);

        // INSERT conflict lowering still runs after native parameter finalization. Until that path
        // becomes native, only binding-free RETURNING items may be appended here.
        var projection = string.Join(", ", returning.Select(item =>
            RenderPostFinalizationProjectionItem(item, command.TargetProvider)));
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

    private static CompiledSqlCommand MarkReturnsRows(
        CompiledSqlCommand command,
        string policyVersion)
    {
        var rewritten = command with
        {
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

    private static string RenderPostFinalizationProjectionItem(
        DmlReturningItem item,
        SqlAgentToolType provider) => item switch
    {
        DmlReturningColumnItem column => CoreIdentifierSqlRenderer.Render(
            column.Identifier,
            provider,
            allowWildcard: false),
        DmlReturningWildcardItem => "*",
        DmlReturningExpressionItem expression => RenderPostFinalizationExpression(expression, provider),
        _ => throw new InvalidOperationException(
            $"Unsupported DML returning projection item {item.GetType().Name}.")
    };

    private static string RenderPostFinalizationExpression(
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
                $"SQL capability '{SqlDmlReturningExpressionCapabilityRules.CapabilityId}' cannot append runtime bindings after native parameter finalization when INSERT conflict lowering is still post-native. Literal-bearing RETURNING expressions with ON CONFLICT remain fail-closed until conflict lowering moves into the native renderer.");
        }

        if (item.Alias is null)
            return fragment.Sql;

        return fragment.Sql + " AS " + CoreIdentifierSqlRenderer.RenderAlias(item.Alias, provider);
    }
}
