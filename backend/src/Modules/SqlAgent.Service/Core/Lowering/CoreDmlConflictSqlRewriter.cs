using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlKata.Compilers;

namespace SqlAgent.Service.Core.Lowering;

/// <summary>
/// Appends the deterministic explicit-target INSERT conflict contract after the ordinary INSERT has
/// been lowered. PostgreSQL and SQLite share this exact subset: explicit conflict columns plus either
/// DO NOTHING or assignments whose right-hand side is a proposed-row (EXCLUDED) column.
/// </summary>
internal static class CoreDmlConflictSqlRewriter
{
    private static readonly Version SqliteUpsertVersion = new(3, 24);

    public static CompiledSqlCommand Apply(
        CompiledSqlCommand command,
        InsertStatement insert,
        SqlProviderCapabilityProfile? targetProfile,
        string policyVersion)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(insert);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);

        if (insert.Conflict is null)
            return command;

        ValidateTargetContract(command.TargetProvider, targetProfile);
        ValidatePortableShape(insert);

        var compiler = SqlKataProviderLowerer.CreateCompiler(command.TargetProvider);
        var suffix = RenderSuffix(insert.Conflict, compiler);
        var rewritten = command with
        {
            Sql = command.Sql.TrimEnd().TrimEnd(';') + suffix,
            PlanFingerprint = string.Empty
        };
        return rewritten with
        {
            PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(rewritten, policyVersion)
        };
    }

    private static string RenderSuffix(InsertConflictClause conflict, Compiler compiler)
    {
        var targets = string.Join(", ", conflict.TargetColumns.Select(column =>
            CoreIdentifierSqlRenderer.Render(column, compiler, allowWildcard: false)));
        if (conflict.Action == InsertConflictActionKind.DoNothing)
            return $" ON CONFLICT ({targets}) DO NOTHING";

        var assignments = string.Join(", ", conflict.Assignments.Select(assignment =>
            CoreIdentifierSqlRenderer.Render(assignment.Column, compiler, allowWildcard: false)
            + " = EXCLUDED."
            + CoreIdentifierSqlRenderer.Render(assignment.ProposedColumn, compiler, allowWildcard: false)));
        return $" ON CONFLICT ({targets}) DO UPDATE SET {assignments}";
    }

    private static void ValidatePortableShape(InsertStatement insert)
    {
        var conflict = insert.Conflict
            ?? throw new SqlCompilationException("INSERT conflict contract is missing.");
        if (insert.Source is not InsertValuesSource values)
        {
            throw new SqlCompilationException(
                "Portable ON CONFLICT is currently limited to INSERT VALUES; INSERT ... SELECT upsert remains fail-closed until source-row cardinality is modeled.");
        }
        if (conflict.TargetColumns.IsDefaultOrEmpty)
            throw new SqlCompilationException("ON CONFLICT requires at least one explicit target column.");

        var insertColumns = new HashSet<string>(
            insert.Columns.Select(RequireSinglePart),
            StringComparer.OrdinalIgnoreCase);
        var conflictColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in conflict.TargetColumns)
        {
            var name = RequireSinglePart(target);
            if (!conflictColumns.Add(name))
                throw new SqlCompilationException($"ON CONFLICT target column '{name}' is declared more than once.");
            if (!insertColumns.Contains(name))
            {
                throw new SqlCompilationException(
                    $"ON CONFLICT target column '{name}' must be explicitly present in the INSERT column list so Core does not depend on provider-default conflict-key values.");
            }
        }

        if (conflict.Action == InsertConflictActionKind.DoNothing)
        {
            if (!conflict.Assignments.IsDefaultOrEmpty)
                throw new SqlCompilationException("ON CONFLICT DO NOTHING cannot carry update assignments.");
            return;
        }

        if (conflict.Action != InsertConflictActionKind.UpdateProposedValues)
            throw new SqlCompilationException($"Unsupported INSERT conflict action {conflict.Action}.");
        if (conflict.Assignments.IsDefaultOrEmpty)
            throw new SqlCompilationException("ON CONFLICT DO UPDATE requires at least one assignment.");
        if (values.Rows.Length != 1)
        {
            throw new SqlCompilationException(
                "Portable ON CONFLICT DO UPDATE currently requires exactly one proposed VALUES row. Without declared unique-index type and collation metadata, Core cannot prove that multiple proposed rows will not resolve to the same target key across providers.");
        }

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in conflict.Assignments)
        {
            var target = RequireSinglePart(assignment.Column);
            var proposed = RequireSinglePart(assignment.ProposedColumn);
            if (!assigned.Add(target))
                throw new SqlCompilationException($"ON CONFLICT DO UPDATE assigns column '{target}' more than once.");
            if (!insertColumns.Contains(proposed))
            {
                throw new SqlCompilationException(
                    $"Proposed-row column '{proposed}' must be explicitly present in the INSERT column list; portable upsert does not depend on target-provider default values.");
            }
        }
    }

    private static string RequireSinglePart(SqlIdentifier identifier)
    {
        if (identifier.Parts.Length != 1
            || identifier.Parts[0].Value == "*" && !identifier.Parts[0].WasQuoted)
        {
            throw new SqlCompilationException(
                "Portable ON CONFLICT columns must be unqualified non-wildcard identifiers.");
        }
        return identifier.Parts[0].Value;
    }

    private static void ValidateTargetContract(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        switch (provider)
        {
            case SqlAgentToolType.Postgres:
                return;
            case SqlAgentToolType.Sqlite when targetProfile?.ServerVersion is { } version
                && version.CompareTo(SqliteUpsertVersion) >= 0:
                return;
            case SqlAgentToolType.Sqlite:
                throw new SqlCompilationException(
                    "SQLite UPSERT requires an explicit target capability profile with ServerVersion 3.24 or newer.");
            case SqlAgentToolType.MySQL:
                throw new SqlCompilationException(
                    "MySQL ON DUPLICATE KEY UPDATE can fire on any UNIQUE or PRIMARY KEY and has no explicit conflict target; Core cannot translate the deterministic target-column contract without unique-index metadata.");
            case SqlAgentToolType.MsSqlServer:
            case SqlAgentToolType.Oracle:
            case SqlAgentToolType.Firebird:
                throw new SqlCompilationException(
                    $"Target provider {provider} requires MERGE-style source/match semantics; portable MERGE remains fail-closed until Core models source-row cardinality and match guarantees.");
            default:
                throw new SqlCompilationException(
                    $"Portable INSERT conflict handling is not represented for target provider {provider}.");
        }
    }
}
