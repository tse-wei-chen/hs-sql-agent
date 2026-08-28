using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Execution;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Adds the portable DML result-row clause after the provider-specific mutation has been lowered.
/// The accepted source surface is still intentionally column-only, but lowering now classifies the
/// projection into explicit semantic item kinds so future expression, OLD/NEW, and OUTPUT work does
/// not have to overload SqlIdentifier shape to represent distinct result-row semantics.
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

        var returning = ReturningColumns(statement);
        if (returning.IsDefaultOrEmpty)
            return command;

        var capabilityError = SqlDmlReturningCapabilityRules.TargetValidationError(
            command.TargetProvider,
            targetProfile);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

        var projectionItems = ClassifyProjection(returning);
        var projection = string.Join(", ", projectionItems.Select(item =>
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

    private static ImmutableArray<SqlIdentifier> ReturningColumns(SqlStatement statement) => statement switch
    {
        InsertStatement insert => insert.Returning,
        UpdateStatement update => update.Returning,
        DeleteStatement delete => delete.Returning,
        _ => ImmutableArray<SqlIdentifier>.Empty
    };

    private static ImmutableArray<ReturningProjectionItem> ClassifyProjection(
        ImmutableArray<SqlIdentifier> columns)
    {
        var items = ImmutableArray.CreateBuilder<ReturningProjectionItem>(columns.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wildcard = false;

        foreach (var column in columns)
        {
            if (column.Parts.Length != 1)
            {
                throw new SqlCompilationException(
                    "Portable DML RETURNING accepts unqualified target columns only.");
            }

            var part = column.Parts[0];
            var isWildcard = part.Value == "*" && !part.WasQuoted;
            wildcard |= isWildcard;
            if (!seen.Add(part.Value))
            {
                throw new SqlCompilationException(
                    $"RETURNING column '{part.Value}' is declared more than once.");
            }

            items.Add(isWildcard
                ? new ReturningWildcardItem(column)
                : new ReturningColumnItem(column));
        }

        if (wildcard && columns.Length != 1)
        {
            throw new SqlCompilationException(
                "RETURNING * cannot be mixed with explicit RETURNING columns in the portable Core contract.");
        }

        return items.ToImmutable();
    }

    private static string RenderProjectionItem(
        ReturningProjectionItem item,
        SqlAgentToolType provider) => item switch
    {
        ReturningColumnItem column => CoreIdentifierSqlRenderer.Render(
            column.Identifier,
            provider,
            allowWildcard: false),
        ReturningWildcardItem wildcard => CoreIdentifierSqlRenderer.Render(
            wildcard.Identifier,
            provider,
            allowWildcard: true),
        _ => throw new InvalidOperationException(
            $"Unsupported DML returning projection item {item.GetType().Name}.")
    };

    private abstract record ReturningProjectionItem(SqlIdentifier Identifier);

    private sealed record ReturningColumnItem(SqlIdentifier Identifier)
        : ReturningProjectionItem(Identifier);

    private sealed record ReturningWildcardItem(SqlIdentifier Identifier)
        : ReturningProjectionItem(Identifier);
}
