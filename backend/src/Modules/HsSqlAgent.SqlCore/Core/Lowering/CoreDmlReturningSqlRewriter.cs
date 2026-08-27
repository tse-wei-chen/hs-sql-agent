using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Execution;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Adds the portable DML result-row clause after the provider-specific mutation has been lowered.
/// The canonical subset is intentionally column-only, so PostgreSQL, SQLite and Firebird can share
/// the same trailing RETURNING shape without provider-default expression or OLD/NEW semantics.
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
        ValidateColumns(returning);

        var projection = string.Join(", ", returning.Select(column =>
            CoreIdentifierSqlRenderer.Render(
                column,
                command.TargetProvider,
                allowWildcard: true)));
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

    private static void ValidateColumns(ImmutableArray<SqlIdentifier> columns)
    {
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
        }

        if (wildcard && columns.Length != 1)
        {
            throw new SqlCompilationException(
                "RETURNING * cannot be mixed with explicit RETURNING columns in the portable Core contract.");
        }
    }
}
