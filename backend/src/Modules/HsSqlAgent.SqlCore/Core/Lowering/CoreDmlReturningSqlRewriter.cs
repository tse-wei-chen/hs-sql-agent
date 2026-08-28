using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Execution;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Finalizes the DML result-row contract after native lowering. RETURNING projection SQL and all
/// runtime bindings are rendered inside the native DML fragment before parameter finalization.
/// Post-native conflict lowering may temporarily place ON CONFLICT/MATCHING after RETURNING; this
/// finalizer only restores provider clause order and marks the command as returning rows.
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

        if (!command.Sql.Contains(" RETURNING ", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlCompilationException(
                "Native DML lowering did not render the canonical RETURNING projection before parameter finalization.");
        }

        var reordered = command with
        {
            Sql = RestoreConflictClauseOrder(command.Sql, command.TargetProvider),
            ReturnsRows = true,
            PlanFingerprint = string.Empty
        };
        return reordered with
        {
            PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(reordered, policyVersion)
        };
    }

    private static ImmutableArray<DmlReturningItem> ReturningItems(SqlStatement statement) => statement switch
    {
        InsertStatement insert => insert.Returning,
        UpdateStatement update => update.Returning,
        DeleteStatement delete => delete.Returning,
        _ => ImmutableArray<DmlReturningItem>.Empty
    };

    private static string RestoreConflictClauseOrder(string sql, SqlAgentToolType provider)
    {
        var returningIndex = sql.IndexOf(" RETURNING ", StringComparison.OrdinalIgnoreCase);
        if (returningIndex < 0)
            return sql;

        var trailingClause = provider switch
        {
            SqlAgentToolType.Postgres or SqlAgentToolType.Sqlite => " ON CONFLICT ",
            SqlAgentToolType.Firebird => " MATCHING ",
            _ => null
        };
        if (trailingClause is null)
            return sql;

        var clauseIndex = sql.IndexOf(
            trailingClause,
            returningIndex + " RETURNING ".Length,
            StringComparison.OrdinalIgnoreCase);
        if (clauseIndex < 0)
            return sql;

        var beforeReturning = sql[..returningIndex];
        var returning = sql[returningIndex..clauseIndex];
        var conflict = sql[clauseIndex..];
        return beforeReturning + conflict + returning;
    }
}
