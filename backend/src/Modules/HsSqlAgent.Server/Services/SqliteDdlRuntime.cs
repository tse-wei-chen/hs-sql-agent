using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Independently approved, file-backed SQLite DDL. The engine's BEGIN IMMEDIATE
/// writer reservation is the commit-time concurrency primitive; PostgreSQL
/// advisory locks, OIDs and transaction claims are not reused.
/// </summary>
public sealed class SqliteDdlRuntime(
    SqliteDdlExecutionOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly SqliteDdlExecutionOptions _options = options ?? new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public bool Enabled => _options.Enabled;

    public async Task<SqliteDdlRuntimeResult> RunAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        DdlActorContext actor,
        bool previewOnly,
        IDdlApprovalProvider? approvalProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!Enabled) return Deny("ddl.disabled", "SQLite live DDL is not enabled by the Host.");
        if (provider.Type != SqlAgentToolType.Sqlite)
            return Deny("ddl.provider_denied", "SQLite DDL requires the SQLite provider.");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(sql)
            || actor?.PrincipalIdentity is null || string.IsNullOrWhiteSpace(actor.PrincipalIdentity)
            || string.IsNullOrWhiteSpace(actor.TargetIdentity)
            || string.IsNullOrWhiteSpace(actor.DatabaseName))
            return Deny("ddl.context_missing", "Complete SQL, physical DB and actor identity required.");
        if (!previewOnly && approvalProvider is null)
            return Deny("ddl.approval_required", "Independent DDL approval is required.");

        CompiledDdlPlan plan;
        string table;
        try
        {
            ValidateOptions();
            plan = SqlCoreFacade.CompileDdl(sql,
                new DdlCompilationOptions(SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite));
            var change = plan.Changes[0];
            if (change.ObjectIdentity.Parts.Count != 2
                || !string.Equals(change.ObjectIdentity.Parts[0], "main", StringComparison.OrdinalIgnoreCase))
                return Deny("ddl.main_identity_required",
                    "SQLite execution requires main.table, not TEMP, attached DB or an unresolved name.");
            table = change.ObjectIdentity.Parts[1];
            if (!Allowed(change.Operation))
                return Deny("ddl.operation_denied", "SQLite DDL operation not enabled by Host.");
            if (!Path.IsPathFullyQualified(actor.DatabaseName))
                return Deny("ddl.database_identity_unverified",
                    "SQLite target identity must be an absolute file path.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Deny("ddl.compile_denied", ex.Message);
        }

        try
        {
            SqliteDdlPhysicalSnapshot before;
            string serverVersion;
            // No database connection is held across human approval.
            await using (var conn = provider.Connections.Create(connectionString))
            {
                await conn.OpenAsync(cancellationToken);
                var profile = RuntimeServerProfileVerifier.Capture(provider.Type, conn);
                if (profile.TargetProfile.ServerVersion is not { Major: >= 3 } version
                    || version.CompareTo(new Version(3, 35)) < 0)
                    return Deny("ddl.version_unproven",
                        "SQLite 3.35+ live runtime version must be proven.");
                serverVersion = profile.ServerVersionIdentity;
                plan = SqlCoreFacade.CompileDdl(sql, new DdlCompilationOptions(
                    SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
                    profile.TargetProfile, profile.TargetProfile));
                before = await ReadSnapshotAsync(conn, null, table, cancellationToken);
                if (!PathEquals(before.DatabaseIdentity, Digest(Path.GetFullPath(actor.DatabaseName))))
                    return Deny("ddl.database_identity_mismatch",
                        "Actual SQLite main file differs from authenticated target.");
                var precondition = await CheckPreconditionAsync(conn, null, plan, before, cancellationToken);
                if (precondition is not null) return Deny("ddl.physical_precondition", precondition);
            }

            var projected = Expected(before, plan);
            var staticPreview = SqlCoreFacade.PreviewDdl(sql,
                new DdlCompilationOptions(SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite));
            var issued = _time.GetUtcNow();
            var expires = issued.Add(_options.ApprovalLifetime);
            var nonce = "ddl_" + Guid.NewGuid().ToString("N");
            var policy = PolicyHash();
            var readable = "SQLite main." + table + "\n"
                + "Before columns: " + Format(before.Columns) + "\n"
                + "Expected columns: " + Format(projected.Columns) + "\n"
                + "Before indexes: " + string.Join(",", before.Indexes.Select(x => x.Name)) + "\n"
                + "Expected indexes: " + string.Join(",", projected.Indexes.Select(x => x.Name))
                + "\nTransactional schema lock. File identity and schema cookie must match at commit.";
            var approvalFingerprint = Digest(JsonSerializer.Serialize(new
            {
                v = "sqlite-ddl-approval-v1",
                plan.PlanFingerprint,
                plan.Command.Sql,
                before.DatabaseIdentity,
                before.Fingerprint,
                before.SchemaVersion,
                actor.PrincipalIdentity,
                actor.TargetIdentity,
                actor.DatabaseName,
                policy,
                nonce,
                issued = issued.ToUnixTimeMilliseconds(),
                expires = expires.ToUnixTimeMilliseconds(),
                readable
            }));
            var preview = new SqliteDdlRuntimePreview(
                staticPreview, before, projected.Columns, projected.Indexes,
                approvalFingerprint, expires, nonce, readable);
            if (previewOnly)
                return new(DdlRuntimeStatus.Preview, preview, "Live read-only SQLite preview; no SQL executed.");

            var request = new DdlApprovalRequest(
                nonce, approvalFingerprint, plan.PlanFingerprint, actor.PrincipalIdentity,
                actor.TargetIdentity, before.DatabaseIdentity, _options.PolicyRevision,
                issued, expires, readable);
            DdlApprovalDecision decision;
            try { decision = await approvalProvider!.RequestAsync(request, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return Deny("ddl.approval_cancelled", "DDL approval cancelled.", preview); }
            if (decision is null || decision.RequestId != nonce
                || decision.ApprovalFingerprint != approvalFingerprint)
                return Deny("ddl.approval_mismatch", "DDL approval does not match exact physical evidence.", preview);
            if (decision.State == DdlApprovalState.Pending)
                return new(DdlRuntimeStatus.Pending, preview,
                    "Pending approval is not executable; submit a new request and re-preview.");
            if (decision.State != DdlApprovalState.Approved
                || string.IsNullOrWhiteSpace(decision.ApproverIdentity)
                || decision.ApproverIdentity == actor.PrincipalIdentity)
                return Deny("ddl.approval_denied", "Independent DDL approval not granted.", preview);
            if (_time.GetUtcNow() >= expires)
                return Deny("ddl.approval_expired", "DDL approval expired; re-preview required.", preview);
            if (policy != PolicyHash() || !Enabled || !Allowed(plan.Changes[0].Operation))
                return Deny("ddl.policy_changed", "DDL policy changed since preview.", preview);

            return await CommitAsync(provider, connectionString, actor,
                plan, preview, serverVersion, policy, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Deny("ddl.cancelled_before_commit", "DDL cancelled before confirmed commit."); }
        catch (Exception)
        { return Deny("ddl.preview_failed", "SQLite live preview or metadata proof failed."); }
    }

    private async Task<SqliteDdlRuntimeResult> CommitAsync(
        ISqlProvider provider, string connectionString, DdlActorContext actor,
        CompiledDdlPlan plan, SqliteDdlRuntimePreview preview,
        string serverVersion, string approvedPolicy, CancellationToken token)
    {
        DbConnection? conn = null;
        DbTransaction? transaction = null;
        var nativeStarted = false;
        var commitStarted = false;
        try
        {
            conn = provider.Connections.Create(connectionString);
            await conn.OpenAsync(token);
            RuntimeServerProfileVerifier.EnsureMatches(conn, serverVersion);

            // Microsoft.Data.Sqlite begins a nondeferred (BEGIN IMMEDIATE)
            // transaction for Serializable by default, reserving SQLite's
            // single writer BEFORE the metadata consistency check.
            transaction = await conn.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var now = _time.GetUtcNow();
            if (now >= preview.ExpiresAt || approvedPolicy != PolicyHash()
                || !Enabled || !Allowed(plan.Changes[0].Operation))
                return await RollbackDeny(transaction, preview,
                    "ddl.approval_expired", "Approval or Host policy expired/changed.");

            var fresh = await ReadSnapshotAsync(conn, transaction,
                preview.Before.TableName, token);
            if (fresh.Fingerprint != preview.Before.Fingerprint
                || fresh.DatabaseIdentity != preview.Before.DatabaseIdentity
                || fresh.DatabaseIdentity != Digest(Path.GetFullPath(actor.DatabaseName)))
                return await RollbackDeny(transaction, preview, "ddl.schema_drift",
                    "Physical SQLite file, schema cookie, table or index changed after approval.");
            var condition = await CheckPreconditionAsync(conn, transaction, plan, fresh, token);
            if (condition is not null)
                return await RollbackDeny(transaction, preview, "ddl.physical_precondition", condition);

            nativeStarted = true;
            // Absolutely no user SQL or raw input reaches DbCommand.
            await using (var cmd = Command(conn, transaction, plan.Command.Sql))
                await cmd.ExecuteNonQueryAsync(token);
            var after = await ReadSnapshotAsync(conn, transaction, fresh.TableName, token);
            var expected = Expected(fresh, plan);
            var valid = after.DatabaseIdentity == fresh.DatabaseIdentity
                && after.SchemaVersion == fresh.SchemaVersion + 1
                && after.TableDefinition is not null
                && SameColumns(after.Columns, expected.Columns)
                && SameIndexes(after.Indexes, expected.Indexes);
            if (!valid)
                return await RollbackFailed(transaction, preview,
                    "ddl.postcondition_mismatch",
                    "SQLite physical schema did not match compiled DDL effects.");
            commitStarted = true;
            await transaction.CommitAsync(token);
            return new(DdlRuntimeStatus.Committed, preview,
                "SQLite DDL transaction committed; physical postcondition verified.");
        }
        catch (Exception)
        {
            if (commitStarted)
                return Unknown(preview, "SQLite COMMIT reply unconfirmed.");
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return nativeStarted
                        ? new(DdlRuntimeStatus.Failed, preview,
                            "SQLite native DDL failed; explicit rollback confirmed.",
                            "ddl.execution_rolled_back")
                        : Deny("ddl.transaction_failed",
                            "SQLite transaction precondition or connection failed; rollback confirmed.", preview);
                }
                catch (Exception)
                { return Unknown(preview, "SQLite rollback reply unconfirmed."); }
            }
            return Deny("ddl.connection_failed",
                "SQLite connection/transaction could not be opened.", preview);
        }
        finally
        {
            if (transaction is not null)
                try { await transaction.DisposeAsync(); } catch (Exception) { }
            if (conn is not null)
                try { await conn.DisposeAsync(); } catch (Exception) { }
        }
    }

    private static async Task<SqliteDdlRuntimeResult> RollbackDeny(
        DbTransaction tx, SqliteDdlRuntimePreview preview, string code, string message)
    {
        try { await tx.RollbackAsync(CancellationToken.None); return Deny(code, message, preview); }
        catch (Exception) { return Unknown(preview, "Rollback result unconfirmed."); }
    }

    private static async Task<SqliteDdlRuntimeResult> RollbackFailed(
        DbTransaction tx, SqliteDdlRuntimePreview preview, string code, string message)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None);
            return new(DdlRuntimeStatus.Failed, preview, message + " Rollback confirmed.", code);
        }
        catch (Exception) { return Unknown(preview, "Post-verification failed and rollback is unconfirmed."); }
    }

    private bool Allowed(DdlSchemaOperation op) => op switch
    {
        DdlSchemaOperation.CreateTable => _options.AllowCreateTable,
        DdlSchemaOperation.AddNullableColumn => _options.AllowAddColumn,
        DdlSchemaOperation.CreateIndex => _options.AllowCreateIndex,
        _ => false
    };

    private void ValidateOptions()
    {
        if (_options.ApprovalLifetime <= TimeSpan.Zero
            || _options.ApprovalLifetime > TimeSpan.FromMinutes(5)
            || _options.ExecutionTimeout <= TimeSpan.Zero
            || _options.ExecutionTimeout > TimeSpan.FromSeconds(15)
            || string.IsNullOrWhiteSpace(_options.PolicyRevision)
            || !(_options.AllowCreateTable || _options.AllowAddColumn || _options.AllowCreateIndex))
            throw new InvalidOperationException("Invalid SQLite DDL Host policy.");
    }

    private string PolicyHash() => Digest(JsonSerializer.Serialize(new
    {
        _options.Enabled, _options.AllowCreateTable, _options.AllowAddColumn,
        _options.AllowCreateIndex, _options.PolicyRevision,
        _options.ApprovalLifetime, _options.ExecutionTimeout
    }));

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static string Normalize(string type) =>
        string.Concat(type.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static string Format(IEnumerable<DdlPhysicalColumn> columns) =>
        string.Join(",", columns.Select(c => c.Name + " " + c.TypeName
            + (c.Nullable ? " NULL" : " NOT NULL")));

    private static (IReadOnlyList<DdlPhysicalColumn> Columns,
        IReadOnlyList<DdlPhysicalIndex> Indexes) Expected(
            SqliteDdlPhysicalSnapshot before, CompiledDdlPlan plan)
    {
        var change = plan.Changes[0];
        var added = change.ColumnDefinitions.Select(c =>
            new DdlPhysicalColumn(c.Name, Normalize(c.TypeName), c.Nullable)).ToArray();
        return change.Operation switch
        {
            DdlSchemaOperation.CreateTable => (added, []),
            DdlSchemaOperation.AddNullableColumn =>
                (before.Columns.Concat(added).ToArray(), before.Indexes),
            DdlSchemaOperation.CreateIndex =>
                (before.Columns, before.Indexes.Concat([
                    new DdlPhysicalIndex(change.IndexName
                        ?? throw new InvalidOperationException("Index name missing."),
                        change.Columns.ToArray(), false, false, false)]).ToArray()),
            _ => throw new InvalidOperationException("Unknown typed DDL operation.")
        };
    }

    private static bool SameColumns(
        IReadOnlyList<DdlPhysicalColumn> actual, IReadOnlyList<DdlPhysicalColumn> expected) =>
        actual.Count == expected.Count
        && actual.Zip(expected).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.OrdinalIgnoreCase)
            && Normalize(pair.First.TypeName) == Normalize(pair.Second.TypeName)
            && pair.First.Nullable == pair.Second.Nullable);

    private static bool SameIndexes(
        IReadOnlyList<DdlPhysicalIndex> actual, IReadOnlyList<DdlPhysicalIndex> expected)
    {
        var a = actual.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var b = expected.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        return a.Length == b.Length && a.Zip(b).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.OrdinalIgnoreCase)
            && pair.First.Unique == pair.Second.Unique
            && pair.First.Partial == pair.Second.Partial
            && pair.First.Expression == pair.Second.Expression
            && pair.First.Columns.SequenceEqual(pair.Second.Columns, StringComparer.OrdinalIgnoreCase));
    }

    private static SqliteDdlRuntimeResult Deny(
        string code, string message, SqliteDdlRuntimePreview? preview = null) =>
        new(DdlRuntimeStatus.Rejected, preview, message, code);

    private static SqliteDdlRuntimeResult Unknown(
        SqliteDdlRuntimePreview preview, string message) =>
        new(DdlRuntimeStatus.OutcomeUnknown, preview,
            message + " Do not automatically retry. Reconcile SQLite schema and file identity.",
            "ddl.outcome_unknown");

    private static async Task<string?> CheckPreconditionAsync(
        DbConnection conn, DbTransaction? tx, CompiledDdlPlan plan,
        SqliteDdlPhysicalSnapshot snapshot, CancellationToken ct)
    {
        var change = plan.Changes[0];
        switch (change.Operation)
        {
            case DdlSchemaOperation.CreateTable:
                return snapshot.TableDefinition is null ? null : "Target SQLite relation already exists.";
            case DdlSchemaOperation.AddNullableColumn:
                if (snapshot.TableDefinition is null) return "Target SQLite table missing.";
                return snapshot.Columns.Any(c => string.Equals(c.Name, change.Columns[0], StringComparison.OrdinalIgnoreCase))
                    ? "Target SQLite column already exists." : null;
            case DdlSchemaOperation.CreateIndex:
                if (snapshot.TableDefinition is null) return "Target SQLite table missing.";
                if (change.Columns.Any(name => snapshot.Columns.All(c =>
                    !string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))))
                    return "Index key column absent.";
                await using (var cmd = Command(conn, tx,
                    "SELECT EXISTS(SELECT 1 FROM main.sqlite_schema WHERE name=@name COLLATE NOCASE)",
                    ("name", change.IndexName ?? "")))
                    return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) != 0
                        ? "SQLite index name already exists in main namespace." : null;
            default: return "Unsupported DDL operation.";
        }
    }

    private static async Task<SqliteDdlPhysicalSnapshot> ReadSnapshotAsync(
        DbConnection conn, DbTransaction? tx, string table, CancellationToken ct)
    {
        // sqlite_schema cookie captures schema-level drift; a physical
        // main-file fingerprint (never the plaintext path) binds the target.
        var databases = new List<(string Name, string File)>();
        await using (var list = Command(conn, tx, "PRAGMA database_list"))
        await using (var rdr = await list.ExecuteReaderAsync(ct))
            while (await rdr.ReadAsync(ct))
                databases.Add((rdr.GetString(1), rdr.GetString(2)));
        if (databases.Count != 1 || databases[0].Name != "main"
            || string.IsNullOrWhiteSpace(databases[0].File)
            || !Path.IsPathFullyQualified(databases[0].File))
            throw new InvalidOperationException(
                "Attached, temporary, in-memory, relative or unverifiable SQLite database forbidden.");
        var file = Path.GetFullPath(databases[0].File);
        var fileInfo = new FileInfo(file);
        if (!fileInfo.Exists || fileInfo.LinkTarget is not null)
            throw new InvalidOperationException("SQLite physical file must exist and not be a symlink.");
        var identity = Digest(file);

        long schemaVersion;
        await using (var cmd = Command(conn, tx, "PRAGMA main.schema_version"))
            schemaVersion = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

        string? tableDefinition = null;
        await using (var cmd = Command(conn, tx,
            "SELECT type, sql FROM main.sqlite_schema WHERE name=@name COLLATE NOCASE", ("name", table)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(0) != "table" || reader.IsDBNull(1))
                    throw new InvalidOperationException(
                        "SQLite target collides with an unproven view/index/virtual object.");
                tableDefinition = reader.GetString(1);
                if (tableDefinition.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Virtual tables are outside the proven DDL subset.");
            }
        }

        var columns = new List<DdlPhysicalColumn>();
        var indexes = new List<DdlPhysicalIndex>();
        if (tableDefinition is not null)
        {
            await using (var cmd = Command(conn, tx,
                "SELECT name, type, \"notnull\", pk, hidden, dflt_value "
                + "FROM pragma_table_xinfo(@table, 'main') ORDER BY cid", ("table", table)))
            await using (var rdr = await cmd.ExecuteReaderAsync(ct))
            {
                while (await rdr.ReadAsync(ct))
                {
                    if (rdr.GetInt64(4) != 0)
                        throw new InvalidOperationException(
                            "Generated or hidden SQLite column metadata not supported.");
                    columns.Add(new DdlPhysicalColumn(rdr.GetString(0),
                        Normalize(rdr.GetString(1)), rdr.GetInt64(2) == 0));
                    if (columns.Count > 128)
                        throw new InvalidOperationException("SQLite live DDL column count exceeds the proven bound.");
                }
            }
            var indexNames = new List<(string Name, bool Unique, bool Partial)>();
            await using (var cmd = Command(conn, tx,
                "SELECT name, \"unique\", origin, partial "
                + "FROM pragma_index_list(@table, 'main') ORDER BY name",
                ("table", table)))
            await using (var rdr = await cmd.ExecuteReaderAsync(ct))
                while (await rdr.ReadAsync(ct))
                {
                    indexNames.Add((rdr.GetString(0), rdr.GetInt64(1) != 0,
                        rdr.GetInt64(3) != 0));
                    if (indexNames.Count > 128)
                        throw new InvalidOperationException("SQLite live DDL index metadata exceeds the proven bound.");
                }
            foreach (var index in indexNames)
            {
                if (index.Partial)
                    throw new InvalidOperationException("Partial SQLite index outside proven schema snapshot.");
                var keys = new List<string>();
                await using (var cmd = Command(conn, tx,
                    "SELECT cid, name, \"key\" FROM pragma_index_xinfo(@name, 'main') ORDER BY seqno",
                    ("name", index.Name)))
                await using (var rdr = await cmd.ExecuteReaderAsync(ct))
                    while (await rdr.ReadAsync(ct))
                        if (rdr.GetInt64(2) != 0)
                        {
                            if (rdr.GetInt64(0) < 0 || rdr.IsDBNull(1))
                                throw new InvalidOperationException("Expression SQLite indexes cannot be proven.");
                            keys.Add(rdr.GetString(1));
                            if (keys.Count > 32)
                                throw new InvalidOperationException("SQLite live DDL index key count exceeds the proven bound.");
                        }
                if (keys.Count == 0)
                    throw new InvalidOperationException("SQLite physical index keys unknown.");
                indexes.Add(new DdlPhysicalIndex(index.Name, keys.AsReadOnly(),
                    index.Unique, false, false));
            }
        }
        var fingerprint = Digest(JsonSerializer.Serialize(new
        {
            identity, schemaVersion, table, tableDefinition, columns, indexes
        }));
        return new SqliteDdlPhysicalSnapshot(identity, "main", schemaVersion,
            table, tableDefinition, columns.AsReadOnly(), indexes.AsReadOnly(), fingerprint);
    }

    private static DbCommand Command(
        DbConnection conn, DbTransaction? tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.CommandTimeout = 15;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}
