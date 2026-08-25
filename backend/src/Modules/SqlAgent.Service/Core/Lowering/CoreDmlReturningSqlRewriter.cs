using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Adds the portable DML result-row clause after the provider-specific mutation has been lowered.
/// The canonical subset is intentionally column-only, so PostgreSQL, SQLite and Firebird can share
/// the same trailing RETURNING shape without provider-default expression or OLD/NEW semantics.
/// </summary>
internal static class CoreDmlReturningSqlRewriter
{
    private static readonly Version SqliteReturningVersion = new(3, 35);
    private static readonly Version FirebirdMultiRowReturningVersion = new(5, 0);

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

        ValidateTargetContract(command.TargetProvider, targetProfile);
        ValidateColumns(returning);

        var compiler = SqlKataProviderLowerer.CreateCompiler(command.TargetProvider);
        var projection = string.Join(", ", returning.Select(column =>
            CoreIdentifierSqlRenderer.Render(column, compiler, allowWildcard: true)));
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

    private static void ValidateTargetContract(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        switch (provider)
        {
            case SqlAgentToolType.Postgres:
                return;
            case SqlAgentToolType.Sqlite when IsAtLeast(targetProfile, SqliteReturningVersion):
                return;
            case SqlAgentToolType.Sqlite:
                throw new SqlCompilationException(
                    "SQLite DML RETURNING requires an explicit target capability profile with ServerVersion 3.35 or newer.");
            case SqlAgentToolType.Firebird when IsAtLeast(targetProfile, FirebirdMultiRowReturningVersion):
                return;
            case SqlAgentToolType.Firebird:
                throw new SqlCompilationException(
                    "Portable multi-row Firebird DSQL RETURNING requires an explicit target capability profile with ServerVersion 5.0 or newer.");
            case SqlAgentToolType.MsSqlServer:
                throw new SqlCompilationException(
                    "SQL Server OUTPUT without INTO is trigger-sensitive and Core has no target-table trigger capability metadata; DML result rows remain fail-closed for SQL Server.");
            case SqlAgentToolType.Oracle:
                throw new SqlCompilationException(
                    "Oracle DML RETURNING requires RETURNING INTO host or bind variables, which are not represented by the Core result-row execution contract.");
            case SqlAgentToolType.MySQL:
                throw new SqlCompilationException(
                    "MySQL has no declared DML RETURNING result-row equivalent in the Core MySQL 8.4 target profile.");
            default:
                throw new SqlCompilationException(
                    $"DML result rows are not represented for target provider {provider}.");
        }
    }

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

    private static bool IsAtLeast(
        SqlProviderCapabilityProfile? profile,
        Version required) =>
        profile?.ServerVersion is { } actual
        && actual.CompareTo(required) >= 0;
}
