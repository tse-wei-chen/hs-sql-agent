using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Execution;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Adds the portable DML result-row clause after the provider-specific mutation has been lowered.
/// Projection semantics are represented directly by Core AST DmlReturningItem kinds so future
/// expression, OLD/NEW, and SQL Server OUTPUT work cannot silently overload SqlIdentifier shape.
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
        _ => throw new InvalidOperationException(
            $"Unsupported DML returning projection item {item.GetType().Name}.")
    };
}
