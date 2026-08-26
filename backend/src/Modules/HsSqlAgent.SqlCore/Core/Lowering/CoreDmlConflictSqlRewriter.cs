using HsSqlAgent.SqlCore.Core.Execution;
using SqlKata.Compilers;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Lowers the deterministic explicit-target INSERT conflict contract after the ordinary INSERT has
/// been compiled. PostgreSQL and SQLite use ON CONFLICT directly. Firebird may use UPDATE OR INSERT
/// only with explicit primary-key assurance. MySQL may use ON DUPLICATE KEY UPDATE only when an
/// explicit target profile supports proposed-row aliases and provider metadata proves that the
/// canonical target is the sole enforced native unique-conflict source.
/// </summary>
internal static class CoreDmlConflictSqlRewriter
{
    private static readonly Version SqliteUpsertVersion = new(3, 24);
    private static readonly Version MySqlProposedRowAliasVersion = new(8, 0, 19);

    public static CompiledSqlCommand Apply(
        CompiledSqlCommand command,
        InsertStatement insert,
        SqlProviderCapabilityProfile? targetProfile,
        DmlConflictTargetAssurance? conflictTargetAssurance,
        string policyVersion)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(insert);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);

        if (insert.Conflict is null)
            return command;

        ValidatePortableShape(insert);
        if (command.TargetProvider == SqlAgentToolType.Firebird)
        {
            return RewriteFirebird(
                command,
                insert,
                conflictTargetAssurance,
                policyVersion);
        }
        if (command.TargetProvider == SqlAgentToolType.MySQL)
        {
            return RewriteMySql(
                command,
                insert,
                targetProfile,
                conflictTargetAssurance,
                policyVersion);
        }

        ValidateTargetContract(command.TargetProvider, targetProfile);
        var compiler = SqlKataProviderLowerer.CreateCompiler(command.TargetProvider);
        var suffix = RenderOnConflictSuffix(insert.Conflict, compiler);
        return RecomputeFingerprint(
            command with
            {
                Sql = command.Sql.TrimEnd().TrimEnd(';') + suffix,
                PlanFingerprint = string.Empty
            },
            policyVersion);
    }

    private static CompiledSqlCommand RewriteFirebird(
        CompiledSqlCommand command,
        InsertStatement insert,
        DmlConflictTargetAssurance? assurance,
        string policyVersion)
    {
        var conflict = insert.Conflict
            ?? throw new SqlCompilationException("INSERT conflict contract is missing.");
        if (conflict.Action != InsertConflictActionKind.UpdateProposedValues)
        {
            throw new SqlCompilationException(
                "Firebird UPDATE OR INSERT has update-or-insert semantics and cannot represent portable ON CONFLICT DO NOTHING; a separate MERGE no-match contract is required.");
        }

        ValidateFirebirdPrimaryKeyTarget(conflict, assurance);
        ValidateFirebirdFullProposedRowUpdate(insert, conflict);

        var sql = command.Sql.TrimEnd().TrimEnd(';');
        if (!sql.StartsWith("INSERT INTO ", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlCompilationException(
                "Firebird conflict lowering expected the Core INSERT backend to emit an INSERT INTO statement.");
        }

        var compiler = SqlKataProviderLowerer.CreateCompiler(SqlAgentToolType.Firebird);
        var matching = string.Join(", ", conflict.TargetColumns.Select(column =>
            CoreIdentifierSqlRenderer.Render(column, compiler, allowWildcard: false)));
        var rewritten = command with
        {
            Sql = "UPDATE OR " + sql + $" MATCHING ({matching})",
            PlanFingerprint = string.Empty
        };
        return RecomputeFingerprint(rewritten, policyVersion);
    }

    private static CompiledSqlCommand RewriteMySql(
        CompiledSqlCommand command,
        InsertStatement insert,
        SqlProviderCapabilityProfile? targetProfile,
        DmlConflictTargetAssurance? assurance,
        string policyVersion)
    {
        var conflict = insert.Conflict
            ?? throw new SqlCompilationException("INSERT conflict contract is missing.");
        if (conflict.Action != InsertConflictActionKind.UpdateProposedValues)
        {
            throw new SqlCompilationException(
                "MySQL INSERT IGNORE is not a portable ON CONFLICT DO NOTHING equivalent because it can suppress errors beyond the explicit conflict target; MySQL DO NOTHING therefore remains fail-closed.");
        }

        ValidateMySqlUniqueKeyTarget(conflict, assurance);

        if (targetProfile?.ServerVersion is not { } version
            || version.CompareTo(MySqlProposedRowAliasVersion) < 0)
        {
            throw new SqlCompilationException(
                "MySQL conflict lowering requires an explicit target capability profile with ServerVersion 8.0.19 or newer so Core can use the proposed-row alias form instead of deprecated VALUES(column) semantics.");
        }

        var compiler = SqlKataProviderLowerer.CreateCompiler(SqlAgentToolType.MySQL);
        var aliasName = CreateMySqlProposedRowAlias(insert);
        var alias = CoreIdentifierSqlRenderer.Render(
            SqlIdentifier.Unquoted(aliasName, SourceSpan.Unknown),
            compiler,
            allowWildcard: false);
        var assignments = string.Join(", ", conflict.Assignments.Select(assignment =>
            CoreIdentifierSqlRenderer.Render(assignment.Column, compiler, allowWildcard: false)
            + " = " + alias + "."
            + CoreIdentifierSqlRenderer.Render(assignment.ProposedColumn, compiler, allowWildcard: false)));
        var sql = command.Sql.TrimEnd().TrimEnd(';');
        var rewritten = command with
        {
            Sql = sql + $" AS {alias} ON DUPLICATE KEY UPDATE {assignments}",
            PlanFingerprint = string.Empty
        };
        return RecomputeFingerprint(rewritten, policyVersion);
    }

    private static void ValidateMySqlUniqueKeyTarget(
        InsertConflictClause conflict,
        DmlConflictTargetAssurance? assurance)
    {
        if (assurance is null || assurance.MatchedUniqueKeyColumns.IsDefaultOrEmpty)
        {
            throw new SqlCompilationException(
                "MySQL ON DUPLICATE KEY UPDATE requires metadata-backed statement assurance proving the explicit conflict target matches a complete enforced unique key and is the sole enforced native conflict source.");
        }
        if (!assurance.IsSoleEnforcedUniqueKey)
        {
            throw new SqlCompilationException(
                "MySQL ON DUPLICATE KEY UPDATE can react to any UNIQUE or PRIMARY KEY conflict. Core requires the matched conflict target to be the sole enforced native unique-conflict source, including no additional richer expression, prefix, partial, or otherwise unsupported enforced unique keys.");
        }

        var target = conflict.TargetColumns
            .Select(RequireSinglePart)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedKey = assurance.MatchedUniqueKeyColumns
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (target.Count != conflict.TargetColumns.Length
            || matchedKey.Count != assurance.MatchedUniqueKeyColumns.Length
            || !target.SetEquals(matchedKey))
        {
            throw new SqlCompilationException(
                "MySQL conflict lowering requires the canonical explicit conflict target to match the complete metadata-resolved unique key exactly.");
        }
    }

    private static string CreateMySqlProposedRowAlias(InsertStatement insert)
    {
        const string preferred = "__core_proposed";
        var tableName = insert.Target.Name.Parts[^1].Value;
        return string.Equals(tableName, preferred, StringComparison.OrdinalIgnoreCase)
            ? preferred + "_row"
            : preferred;
    }

    private static void ValidateFirebirdPrimaryKeyTarget(
        InsertConflictClause conflict,
        DmlConflictTargetAssurance? assurance)
    {
        if (assurance is null || assurance.PrimaryKeyColumns.IsDefaultOrEmpty)
        {
            throw new SqlCompilationException(
                "Firebird UPDATE OR INSERT requires metadata-backed conflict-target assurance proving MATCHING equals the resolved primary key; absent assurance remains fail-closed because non-unique MATCHING can update multiple rows.");
        }

        var target = conflict.TargetColumns
            .Select(RequireSinglePart)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var primaryKey = assurance.PrimaryKeyColumns
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (target.Count != conflict.TargetColumns.Length
            || primaryKey.Count != assurance.PrimaryKeyColumns.Length
            || !target.SetEquals(primaryKey))
        {
            throw new SqlCompilationException(
                "Firebird UPDATE OR INSERT requires the canonical conflict target to match the complete resolved primary key exactly; general UNIQUE-key and non-unique MATCHING metadata are not represented yet.");
        }
    }

    private static void ValidateFirebirdFullProposedRowUpdate(
        InsertStatement insert,
        InsertConflictClause conflict)
    {
        if (conflict.Assignments.Length != insert.Columns.Length)
        {
            throw new SqlCompilationException(
                "Firebird UPDATE OR INSERT updates every supplied INSERT column on a match. Core therefore requires one same-column proposed-row assignment for every INSERT column so partial-update semantics cannot drift.");
        }

        var insertColumns = insert.Columns
            .Select(RequireSinglePart)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in conflict.Assignments)
        {
            var target = RequireSinglePart(assignment.Column);
            var proposed = RequireSinglePart(assignment.ProposedColumn);
            if (!string.Equals(target, proposed, StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlCompilationException(
                    "Firebird UPDATE OR INSERT can mirror the portable conflict contract only when each assignment is target = proposed-row target for the same column.");
            }
            if (!assigned.Add(target) || !insertColumns.Contains(target))
            {
                throw new SqlCompilationException(
                    $"Firebird UPDATE OR INSERT assignment column '{target}' must occur exactly once in the INSERT column list.");
            }
        }

        if (!assigned.SetEquals(insertColumns))
        {
            throw new SqlCompilationException(
                "Firebird UPDATE OR INSERT requires conflict assignments to cover the complete INSERT column set.");
        }
    }

    private static string RenderOnConflictSuffix(InsertConflictClause conflict, Compiler compiler)
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
                "Portable INSERT conflict handling is currently limited to INSERT VALUES; INSERT ... SELECT upsert remains fail-closed until source-row cardinality is modeled.");
        }
        if (conflict.TargetColumns.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT conflict handling requires at least one explicit target column.");

        var insertColumns = new HashSet<string>(
            insert.Columns.Select(RequireSinglePart),
            StringComparer.OrdinalIgnoreCase);
        var conflictColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in conflict.TargetColumns)
        {
            var name = RequireSinglePart(target);
            if (!conflictColumns.Add(name))
                throw new SqlCompilationException($"INSERT conflict target column '{name}' is declared more than once.");
            if (!insertColumns.Contains(name))
            {
                throw new SqlCompilationException(
                    $"INSERT conflict target column '{name}' must be explicitly present in the INSERT column list so Core does not depend on provider-default conflict-key values.");
            }
        }

        if (conflict.Action == InsertConflictActionKind.DoNothing)
        {
            if (!conflict.Assignments.IsDefaultOrEmpty)
                throw new SqlCompilationException("INSERT conflict DO NOTHING cannot carry update assignments.");
            return;
        }

        if (conflict.Action != InsertConflictActionKind.UpdateProposedValues)
            throw new SqlCompilationException($"Unsupported INSERT conflict action {conflict.Action}.");
        if (conflict.Assignments.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT conflict DO UPDATE requires at least one assignment.");
        if (values.Rows.Length != 1)
        {
            throw new SqlCompilationException(
                "Portable INSERT conflict DO UPDATE currently requires exactly one proposed VALUES row. Without declared unique-index type and collation metadata, Core cannot prove that multiple proposed rows will not resolve to the same target key across providers.");
        }

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in conflict.Assignments)
        {
            var target = RequireSinglePart(assignment.Column);
            var proposed = RequireSinglePart(assignment.ProposedColumn);
            if (!assigned.Add(target))
                throw new SqlCompilationException($"INSERT conflict DO UPDATE assigns column '{target}' more than once.");
            if (!insertColumns.Contains(proposed))
            {
                throw new SqlCompilationException(
                    $"Proposed-row column '{proposed}' must be explicitly present in the INSERT column list; portable upsert does not depend on target-provider default values.");
            }
        }
    }

    private static CompiledSqlCommand RecomputeFingerprint(
        CompiledSqlCommand command,
        string policyVersion) => command with
    {
        PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(command, policyVersion)
    };

    private static string RequireSinglePart(SqlIdentifier identifier)
    {
        if (identifier.Parts.Length != 1
            || identifier.Parts[0].Value == "*" && !identifier.Parts[0].WasQuoted)
        {
            throw new SqlCompilationException(
                "Portable INSERT conflict columns must be unqualified non-wildcard identifiers.");
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
            case SqlAgentToolType.MsSqlServer:
            case SqlAgentToolType.Oracle:
                throw new SqlCompilationException(
                    $"Target provider {provider} requires MERGE-style source/match semantics; portable MERGE remains fail-closed until Core models source-row cardinality and match guarantees.");
            default:
                throw new SqlCompilationException(
                    $"Portable INSERT conflict handling is not represented for target provider {provider}.");
        }
    }
}
