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
/// PostgreSQL-only, fail-closed Host DDL execution. Each request is ephemeral:
/// there is no reusable approval token, external execution primitive, raw SQL
/// fallback, or shared DML/DDL transaction policy. Physical state is read on a
/// live connection and rechecked under transaction locks before one statement.
/// </summary>
public sealed class PostgresDdlRuntime(
    PostgresDdlExecutionOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly PostgresDdlExecutionOptions _options = options ?? new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public bool Enabled => _options.Enabled;

    public async Task<DdlRuntimeResult> RunAsync(
        ISqlProvider provider,
        string connectionString,
        string sql,
        DdlActorContext actor,
        bool previewOnly,
        IDdlApprovalProvider? approvalProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_options.Enabled)
            return Rejected("ddl.disabled", "PostgreSQL live DDL must be explicitly enabled by the Host.");
        if (provider.Type != SqlAgentToolType.Postgres)
            return Rejected("ddl.provider_denied", "Live DDL is available only for PostgreSQL.");
        if (string.IsNullOrWhiteSpace(sql)
            || string.IsNullOrWhiteSpace(connectionString)
            || actor is null
            || string.IsNullOrWhiteSpace(actor.PrincipalIdentity)
            || string.IsNullOrWhiteSpace(actor.TargetIdentity)
            || string.IsNullOrWhiteSpace(actor.DatabaseName))
            return Rejected("ddl.context_missing", "A complete DDL SQL, database and actor identity is required.");
        if (!previewOnly && approvalProvider is null)
            return Rejected("ddl.approval_required", "An independent DDL approval provider is required.");

        CompiledDdlPlan plan;
        string schema, table;
        try
        {
            // Compile BEFORE opening a connection. Only the strict G1 typed
            // operation family, not the user's SQL string, reaches execution.
            plan = SqlCoreFacade.CompileDdl(
                sql, new DdlCompilationOptions(SqlAgentToolType.Postgres, SqlAgentToolType.Postgres));
            var change = plan.Changes[0];
            if (change.ObjectIdentity.Parts.Count != 2)
                return Rejected("ddl.qualified_identity_required",
                    "Live DDL requires an explicit schema.table, not an unqualified search_path target.");
            schema = change.ObjectIdentity.Parts[0];
            table = change.ObjectIdentity.Parts[1];
            if (!_options.AllowedSchemas.Any(allowed => String.Equals(allowed, schema, StringComparison.Ordinal))
                || schema is "pg_catalog" or "information_schema"
                || schema.StartsWith("pg_temp", StringComparison.Ordinal))
                return Rejected("ddl.schema_denied", "Target schema is not explicitly allowlisted for DDL.");
            if (!AllowedOperation(change.Operation))
                return Rejected("ddl.operation_denied", "This DDL operation is not enabled by the Host.");
            ValidateConfiguration();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Rejected("ddl.compile_denied", ex.Message);
        }

        try
        {
            // Release the read-only preview connection BEFORE interactive
            // approval. Approval can take minutes and must not consume an
            // unnecessary PostgreSQL pool connection or transaction.
            VerifiedRuntimeServerProfile profile;
            DdlPhysicalSnapshot initial;
            await using (var connection = provider.Connections.Create(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                profile = RuntimeServerProfileVerifier.Capture(provider.Type, connection);
                if (profile.TargetProfile.ServerVersion is null
                    || profile.TargetProfile.ServerVersion.Major < 12)
                    return Rejected("ddl.version_unproven", "PostgreSQL 12 or later runtime server-version proof is required.");
                plan = SqlCoreFacade.CompileDdl(sql,
                    new DdlCompilationOptions(SqlAgentToolType.Postgres,
                        SqlAgentToolType.Postgres, profile.TargetProfile, profile.TargetProfile));
                initial = await ReadSnapshotAsync(connection, null, schema, table, cancellationToken);
                if (!string.Equals(initial.DatabaseName, actor.DatabaseName, StringComparison.Ordinal))
                    return Rejected("ddl.database_identity_mismatch",
                        "Live database name does not match the authenticated MCP target database identity.");
                var precondition = await VerifyPreconditionAsync(
                    connection, null, initial, plan, cancellationToken);
                if (precondition is not null)
                    return Rejected("ddl.physical_precondition", precondition);
            }

            var projected = ProjectExpected(initial, plan);
            var staticPreview = SqlCoreFacade.PreviewDdl(sql,
                new DdlCompilationOptions(SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                    profile.TargetProfile, profile.TargetProfile));
            var now = _time.GetUtcNow();
            var expiry = now + _options.ApprovalLifetime;
            var requestId = "ddl_" + Guid.NewGuid().ToString("N");
            var policy = PolicyMaterial();
            var readable = HumanReadableDiff(initial, projected.Columns, projected.Indexes, plan);
            var fingerprint = Digest(JsonSerializer.Serialize(new
            {
                version = "pg-ddl-approval-v1",
                plan.PlanFingerprint,
                nativeSql = plan.Command.Sql,
                beforeFingerprint = initial.Fingerprint,
                initial.ServerIdentity,
                actor.PrincipalIdentity,
                actor.TargetIdentity,
                actor.DatabaseName,
                policy,
                requestId,
                issuedAt = now.ToUnixTimeMilliseconds(),
                expiresAt = expiry.ToUnixTimeMilliseconds(),
                readable
            }));
            var preview = new DdlRuntimePreview(staticPreview, initial,
                projected.Columns, projected.Indexes, fingerprint, expiry, requestId, readable);
            if (previewOnly)
                return new DdlRuntimeResult(DdlRuntimeStatus.Preview, preview,
                    "Read-only live PostgreSQL metadata preview; no DDL was executed or approved.");

            var request = new DdlApprovalRequest(
                requestId, fingerprint, plan.PlanFingerprint, actor.PrincipalIdentity,
                actor.TargetIdentity, initial.ServerIdentity, _options.PolicyRevision,
                now, expiry, readable);
            DdlApprovalDecision decision;
            try
            {
                decision = await approvalProvider!.RequestAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Rejected("ddl.approval_cancelled", "DDL approval was cancelled.", preview);
            }
            if (decision is null
                || !string.Equals(decision.RequestId, requestId, StringComparison.Ordinal)
                || !string.Equals(decision.ApprovalFingerprint, fingerprint, StringComparison.Ordinal))
                return Rejected("ddl.approval_mismatch", "DDL approval did not bind to the exact live schema evidence.", preview);
            if (decision.State == DdlApprovalState.Pending)
                return new DdlRuntimeResult(DdlRuntimeStatus.Pending, preview,
                    "DDL approval pending. This ephemeral request cannot be resumed to execute.");
            if (decision.State != DdlApprovalState.Approved
                || string.IsNullOrWhiteSpace(decision.ApproverIdentity)
                || string.Equals(decision.ApproverIdentity, actor.PrincipalIdentity, StringComparison.Ordinal))
                return Rejected("ddl.approval_denied", "DDL approval was not independently granted.", preview);
            if (_time.GetUtcNow() >= expiry)
                return Rejected("ddl.approval_expired", "DDL approval expired. Re-preview required.", preview);
            if (!string.Equals(policy, PolicyMaterial(), StringComparison.Ordinal)
                || !_options.Enabled
                || !_options.AllowedSchemas.Any(allowed => String.Equals(allowed, schema, StringComparison.Ordinal))
                || !AllowedOperation(plan.Changes[0].Operation))
                return Rejected("ddl.policy_changed", "DDL Host policy changed after approval.", preview);

            // Do not use the preview connection for the commit. The connection
            // is re-created and the physical identity re-proven.
            return await CommitOnceAsync(provider, connectionString, plan, initial, preview,
                profile.ServerVersionIdentity, policy, actor, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Rejected("ddl.cancelled_before_execute",
                "DDL request was cancelled before entering execution.");
        }
        catch (Exception)
        {
            // This catch surrounds read-only compilation and snapshot reads.
            // The transaction commit path catches uncertainty independently.
            return Rejected("ddl.preview_failed",
                "Live PostgreSQL DDL preview or physical identity verification failed.");
        }
    }

    private async Task<DdlRuntimeResult> CommitOnceAsync(
        ISqlProvider provider,
        string connectionString,
        CompiledDdlPlan plan,
        DdlPhysicalSnapshot approvedBefore,
        DdlRuntimePreview preview,
        string approvedServerVersion,
        string approvedPolicy,
        DdlActorContext actor,
        CancellationToken cancellationToken)
    {
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        var nativeCommandStarted = false;
        var commitStarted = false;
        try
        {
            connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(cancellationToken);
            RuntimeServerProfileVerifier.EnsureMatches(connection, approvedServerVersion);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            {
                // Conservative locks, not a promise of external global CAS.
                // For ALTER/INDEX the table lock is acquired BEFORE readback.
                await ExecuteHostSqlAsync(connection, transaction,
                    "SET LOCAL lock_timeout = '3s'", cancellationToken);
                var statementTimeoutMs = Math.Clamp(
                    (int)_options.ExecutionTimeout.TotalMilliseconds, 1, 15000);
                await ExecuteHostSqlAsync(connection, transaction,
                    "SET LOCAL statement_timeout = '" + statementTimeoutMs + "ms'", cancellationToken);
                var schema = approvedBefore.SchemaName;
                var table = approvedBefore.TableName;
                await ExecuteHostSqlAsync(connection, transaction,
                    "SELECT pg_advisory_xact_lock(hashtext(@schema), hashtext(@table))",
                    cancellationToken, ("schema", schema), ("table", table));
                if (plan.Changes[0].Operation != DdlSchemaOperation.CreateTable)
                    await ExecuteHostSqlAsync(connection, transaction,
                        "LOCK TABLE " + Quote(schema) + "." + Quote(table)
                        + " IN ACCESS EXCLUSIVE MODE", cancellationToken);

                var fresh = await ReadSnapshotAsync(connection, transaction, schema, table, cancellationToken);
                if (!string.Equals(approvedBefore.Fingerprint, fresh.Fingerprint, StringComparison.Ordinal)
                    || !string.Equals(approvedBefore.ServerIdentity, fresh.ServerIdentity, StringComparison.Ordinal)
                    || !string.Equals(fresh.DatabaseName, actor.DatabaseName, StringComparison.Ordinal)
                    || !string.Equals(approvedPolicy, PolicyMaterial(), StringComparison.Ordinal)
                    || _time.GetUtcNow() >= preview.ExpiresAt)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return Rejected("ddl.schema_drift",
                        "Live target, plan, approval expiration, policy or schema changed before DDL execution.", preview);
                }
                var error = await VerifyPreconditionAsync(
                    connection, transaction, fresh, plan, cancellationToken);
                if (error is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return Rejected("ddl.precondition_changed", error, preview);
                }

                nativeCommandStarted = true;
                await ExecuteHostSqlAsync(connection, transaction, plan.Command.Sql, cancellationToken);
                var after = await ReadSnapshotAsync(connection, transaction, schema, table, cancellationToken);
                if (!MatchesExpected(after, plan, approvedBefore))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return new DdlRuntimeResult(DdlRuntimeStatus.Failed, preview,
                        "DDL postcondition did not match typed schema plan; transaction rolled back.",
                        "ddl.postcondition_mismatch");
                }
                commitStarted = true;
                await transaction.CommitAsync(cancellationToken);
                return new DdlRuntimeResult(DdlRuntimeStatus.Committed, preview,
                    "PostgreSQL transaction committed after verifying the typed schema postcondition.");
            }
        }
        catch (Exception)
        {
            // COMMIT reply loss is NEVER mapped to a proven failure/rollback.
            // Before COMMIT a successful explicit rollback is proof that
            // database changes from the transaction were not committed.
            if (commitStarted)
                return Unknown(preview, "DDL COMMIT outcome could not be confirmed.");
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return new DdlRuntimeResult(
                        nativeCommandStarted ? DdlRuntimeStatus.Failed : DdlRuntimeStatus.Rejected,
                        preview,
                        nativeCommandStarted
                            ? "DDL execution failed; PostgreSQL transaction rollback confirmed."
                            : "DDL transaction precondition or lock check failed; rollback confirmed.",
                        nativeCommandStarted ? "ddl.execution_rolled_back" : "ddl.commit_precondition");
                }
                catch (Exception)
                {
                    return Unknown(preview, "DDL rollback outcome could not be confirmed.");
                }
            }
            return Rejected("ddl.connection_failed",
                "PostgreSQL connection failed before the DDL transaction began.", preview);
        }
        finally
        {
            // Disposal occurs AFTER the rollback catch, while the connection
            // is still usable. Never infer rollback from disposal alone.
            if (transaction is not null)
                try { await transaction.DisposeAsync(); } catch (Exception) { }
            if (connection is not null)
                try { await connection.DisposeAsync(); } catch (Exception) { }
        }
    }

    private bool AllowedOperation(DdlSchemaOperation op) => op switch
    {
        DdlSchemaOperation.CreateTable => _options.AllowCreateTable,
        DdlSchemaOperation.AddNullableColumn => _options.AllowAddColumn,
        DdlSchemaOperation.CreateIndex => _options.AllowCreateIndex,
        DdlSchemaOperation.DropTable => _options.AllowDropEmptyTable,
        _ => false
    };

    private void ValidateConfiguration()
    {
        if (_options.AllowedSchemas.Count == 0
            || _options.ApprovalLifetime <= TimeSpan.Zero
            || _options.ApprovalLifetime > TimeSpan.FromMinutes(5)
            || _options.ExecutionTimeout <= TimeSpan.Zero
            || _options.ExecutionTimeout > TimeSpan.FromSeconds(15)
            || string.IsNullOrWhiteSpace(_options.PolicyRevision))
            throw new InvalidOperationException("PostgreSQL DDL explicit Host policy is invalid.");
    }

    private string PolicyMaterial() =>
        Digest(JsonSerializer.Serialize(new
        {
            _options.Enabled,
            _options.AllowCreateTable,
            _options.AllowAddColumn,
            _options.AllowCreateIndex,
            _options.AllowDropEmptyTable,
            _options.PolicyRevision,
            schemas = _options.AllowedSchemas.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            _options.ApprovalLifetime,
            _options.ExecutionTimeout
        }));

    private static string Quote(string name) =>
        "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static DdlRuntimeResult Rejected(string code, string message,
        DdlRuntimePreview? preview = null) =>
        new(DdlRuntimeStatus.Rejected, preview, message, code);

    private static DdlRuntimeResult Unknown(DdlRuntimePreview preview, string message) =>
        new(DdlRuntimeStatus.OutcomeUnknown, preview,
            message + " Do NOT automatically retry; reconcile actual PostgreSQL metadata.",
            "ddl.outcome_unknown");

    private static string NormalType(string sqlType)
    {
        var value = sqlType.ToUpperInvariant();
        return value switch
        {
            "INT" or "INTEGER" => "integer",
            "BIGINT" => "bigint",
            "SMALLINT" => "smallint",
            "BOOLEAN" => "boolean",
            "DATE" => "date",
            "TEXT" => "text",
            "TIMESTAMP" => "timestamp without time zone",
            _ when value.StartsWith("VARCHAR(", StringComparison.Ordinal) =>
                "character varying" + value["VARCHAR".Length..].ToLowerInvariant(),
            _ when value.StartsWith("CHAR(", StringComparison.Ordinal) =>
                "character" + value["CHAR".Length..].ToLowerInvariant(),
            _ => throw new InvalidOperationException("Unverified PostgreSQL column type.")
        };
    }

    private static (IReadOnlyList<DdlPhysicalColumn> Columns,
        IReadOnlyList<DdlPhysicalIndex> Indexes)
        ProjectExpected(DdlPhysicalSnapshot before, CompiledDdlPlan plan)
    {
        var change = plan.Changes[0];
        var defs = change.ColumnDefinitions
            .Select(c => new DdlPhysicalColumn(c.Name, NormalType(c.TypeName), c.Nullable)).ToArray();
        return change.Operation switch
        {
            DdlSchemaOperation.CreateTable => (defs, []),
            DdlSchemaOperation.AddNullableColumn =>
                (before.Columns.Concat(defs).ToArray(), before.Indexes),
            DdlSchemaOperation.CreateIndex =>
                (before.Columns, before.Indexes.Concat([
                    new DdlPhysicalIndex(change.IndexName
                        ?? throw new InvalidOperationException("An index name is required."),
                        change.Columns.ToArray(), false, false, false)]).ToArray()),
            DdlSchemaOperation.DropTable => ([], []),
            _ => throw new InvalidOperationException("Unsupported closed DDL operation.")
        };
    }

    private static bool MatchesExpected(DdlPhysicalSnapshot after,
        CompiledDdlPlan plan, DdlPhysicalSnapshot before)
    {
        if (plan.Changes[0].Operation == DdlSchemaOperation.DropTable)
            return after.SchemaOid == before.SchemaOid
                && after.ServerIdentity == before.ServerIdentity
                && before.TableOid.HasValue && !after.TableOid.HasValue
                && after.Columns.Count == 0 && after.Indexes.Count == 0;
        if (after.SchemaOid != before.SchemaOid
            || !string.Equals(after.ServerIdentity, before.ServerIdentity, StringComparison.Ordinal)
            || after.TableOid is null
            || (plan.Changes[0].Operation != DdlSchemaOperation.CreateTable
                && after.TableOid != before.TableOid))
            return false;
        var expected = ProjectExpected(before, plan);
        return after.Columns.SequenceEqual(expected.Columns)
               && after.Indexes.OrderBy(x => x.Name, StringComparer.Ordinal)
                   .SequenceEqual(expected.Indexes.OrderBy(x => x.Name, StringComparer.Ordinal),
                       PhysicalIndexComparer.Instance);
    }

    private sealed class PhysicalIndexComparer : IEqualityComparer<DdlPhysicalIndex>
    {
        public static readonly PhysicalIndexComparer Instance = new();
        public bool Equals(DdlPhysicalIndex? a, DdlPhysicalIndex? b) =>
            a is not null && b is not null
            && a.Name == b.Name && a.Unique == b.Unique
            && a.Partial == b.Partial && a.Expression == b.Expression
            && a.Columns.SequenceEqual(b.Columns);
        public int GetHashCode(DdlPhysicalIndex item) => StringComparer.Ordinal.GetHashCode(item.Name);
    }

    private static string HumanReadableDiff(
        DdlPhysicalSnapshot before, IReadOnlyList<DdlPhysicalColumn> afterCols,
        IReadOnlyList<DdlPhysicalIndex> afterIndexes, CompiledDdlPlan plan)
    {
        static string Columns(IEnumerable<DdlPhysicalColumn> columns) =>
            string.Join(", ", columns.Select(c => c.Name + " " + c.TypeName
                + (c.Nullable ? " NULL" : " NOT NULL")));
        static string Indexes(IEnumerable<DdlPhysicalIndex> items) =>
            string.Join(", ", items.Select(x => x.Name + "(" + string.Join(", ", x.Columns) + ")"));
        var destructive = plan.Changes[0].Operation == DdlSchemaOperation.DropTable
            ? "HIGH RISK: DELETE the physical table definition permanently after COMMIT. "
              + "No data rows may exist: verified at preview and rechecked under "
              + "ACCESS EXCLUSIVE lock before DROP TABLE RESTRICT. "
              + "Object privileges and application dependencies may be impacted. "
              + "The operation requires separate destructive opt-in and independent approval.\\n"
            : string.Empty;
        return destructive + "DDL change: " + plan.Changes[0].Operation + "\n"
            + "Physical target: " + before.DatabaseName + "/"
            + before.SchemaName + "." + before.TableName + "\n"
            + "Before columns: " + (before.TableOid is null ? "<table absent>" : Columns(before.Columns)) + "\n"
            + "Expected columns: " + Columns(afterCols) + "\n"
            + "Before indexes: " + Indexes(before.Indexes) + "\n"
            + "Expected indexes: " + Indexes(afterIndexes) + "\n"
            + "Impact: possible exclusive schema lock, unknown duration and affected rows. "
            + "Approval never authorizes other statements or a different database. "
            + "A commit must independently recheck identity, schema and preconditions.";
    }

    private static async Task<string?> VerifyPreconditionAsync(
        DbConnection connection, DbTransaction? transaction,
        DdlPhysicalSnapshot snapshot, CompiledDdlPlan plan,
        CancellationToken cancellationToken)
    {
        var change = plan.Changes[0];
        if (snapshot.SchemaOid is null)
            return "Target PostgreSQL schema does not exist.";
        // Unsupported event triggers can execute arbitrary SQL as a side
        // effect of DDL. Refuse rather than claim the projected delta is
        // complete. Rechecked in the commit transaction.
        var eventTrigger = await ScalarAsync(connection, transaction,
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger "
            + "WHERE evtenabled <> 'D')", cancellationToken);
        if (eventTrigger is true)
            return "Enabled PostgreSQL DDL event triggers are outside the proven execution subset.";
        var name = change.IndexName;
        switch (change.Operation)
        {
            case DdlSchemaOperation.CreateTable:
                return snapshot.TableOid is null ? null : "Target relation already exists.";
            case DdlSchemaOperation.AddNullableColumn:
                if (snapshot.TableOid is null) return "Target table does not exist.";
                if (snapshot.Columns.Any(c => c.Name == change.Columns[0]))
                    return "Target column already exists.";
                return null;
            case DdlSchemaOperation.DropTable:
                return await ProveEmptyDropAsync(connection, transaction, snapshot, cancellationToken);
            case DdlSchemaOperation.CreateIndex:
                if (snapshot.TableOid is null) return "Target table does not exist.";
                if (change.Columns.Any(n => snapshot.Columns.All(c => c.Name != n)))
                    return "One or more target index columns are absent.";
                var collision = await ScalarAsync(connection, transaction,
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c "
                    + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                    + "WHERE n.nspname = @schema AND c.relname = @name)",
                    cancellationToken, ("schema", snapshot.SchemaName), ("name", name));
                return collision is true ? "Index name already exists in the target schema." : null;
            default:
                return "DDL operation has no proven physical precondition.";
        }
    }

    /// <summary>
    /// The G6 destructive capability is intentionally bounded to an ordinary,
    /// empty, non-partitioned, owner-controlled 1–16 INTEGER-column table.
    /// This proof is repeated after ACCESS EXCLUSIVE lock inside the commit
    /// transaction. RESTRICT remains a final server-side dependency fence.
    /// </summary>
    private static async Task<string?> ProveEmptyDropAsync(
        DbConnection connection, DbTransaction? transaction,
        DdlPhysicalSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.TableOid is not long oid
            || snapshot.Columns.Count is < 1 or > 16
            || snapshot.Columns.Any(c => c.TypeName != "integer")
            || snapshot.Indexes.Count != 0)
            return "Destructive DROP requires a simple 1–16 INTEGER-column table without indexes.";

        // No implicit trust from SqlCore's virtual Catalog or from stale preview.
        // Direct table ownership, persistence, RLS, grants and inheritance are
        // proven using PostgreSQL's own catalog rather than parsing client SQL.
        var simple = await ScalarAsync(connection, transaction,
            "SELECT c.relkind='r' AND c.relpersistence='p' "
            + "AND NOT c.relispartition AND NOT c.relhassubclass "
            + "AND NOT c.relrowsecurity AND NOT c.relforcerowsecurity "
            + "AND NOT c.relhastriggers AND c.relacl IS NULL "
            + "AND c.relowner=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=current_user) "
            + "FROM pg_catalog.pg_class c WHERE c.oid=@oid::oid",
            cancellationToken, ("oid", oid));
        if (simple is not true)
            return "Destructive DROP table owner, persistence or security posture is unproven.";

        var hazards = await ScalarAsync(connection, transaction,
            "SELECT "
            + "EXISTS(SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid=@oid::oid OR confrelid=@oid::oid) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_index WHERE indrelid=@oid::oid) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_inherits WHERE inhrelid=@oid::oid OR inhparent=@oid::oid) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_policy WHERE polrelid=@oid::oid) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_rewrite WHERE ev_class=@oid::oid) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid=@oid::oid) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_attribute "
            + "WHERE attrelid=@oid::oid AND attnum>0 AND NOT attisdropped "
            + "AND (atthasdef OR attidentity<>'' OR attgenerated<>'')) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_depend d "
            + "JOIN pg_catalog.pg_rewrite r ON r.oid=d.objid "
            + "WHERE d.refobjid=@oid::oid AND d.classid='pg_catalog.pg_rewrite'::regclass "
            + "AND r.ev_class<>@oid::oid) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_depend d "
            + "JOIN pg_catalog.pg_class owned ON owned.oid=d.objid "
            + "WHERE d.refobjid=@oid::oid AND owned.relkind='S') "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_depend d "
            + "WHERE d.classid='pg_catalog.pg_class'::regclass "
            + "AND d.objid=@oid::oid AND d.deptype='e') "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_publication_rel WHERE prrelid=@oid::oid) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_publication WHERE puballtables) "
            + "OR EXISTS(SELECT 1 FROM pg_catalog.pg_publication_namespace WHERE pnnspid=@schemaoid::oid)",
            cancellationToken, ("oid", oid),
            ("schemaoid", snapshot.SchemaOid ?? throw new InvalidOperationException(
                "Missing destructive target namespace.")));
        if (hazards is not false)
            return "Destructive DROP cannot prove the absence of dependent, replicated or special schema objects.";

        // The name is from the F# verified simple-ASCII schema/table identity,
        // not unchecked SQL. Commit-time ACCESS EXCLUSIVE locks out writers.
        var hasRows = await ScalarAsync(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM " + Quote(snapshot.SchemaName) + "."
            + Quote(snapshot.TableName) + " LIMIT 1)", cancellationToken);
        return hasRows is false ? null
            : "Destructive DROP denied: real table must contain zero data rows.";
    }

    private static async Task<DdlPhysicalSnapshot> ReadSnapshotAsync(
        DbConnection connection, DbTransaction? transaction,
        string schema, string table, CancellationToken cancellationToken)
    {
        var identity = await ReadIdentityAsync(connection, transaction, cancellationToken);
        // One catalog statement proves namespace OID and relation OID
        // together; unqualified search_path never participates.
        var relation = await ScalarAsync(connection, transaction,
            "SELECT n.oid::text || ':' || COALESCE(c.oid::text, '') "
            + "|| ':' || COALESCE(c.relkind::text, '') "
            + "FROM pg_catalog.pg_namespace n "
            + "LEFT JOIN pg_catalog.pg_class c ON c.relnamespace = n.oid "
            + "AND c.relname = @table "
            + "WHERE n.nspname = @schema",
            cancellationToken, ("schema", schema), ("table", table));
        long? schemaOid = null;
        long? oid = null;
        if (relation is not null && relation is not DBNull)
        {
            var parts = Convert.ToString(relation,
                System.Globalization.CultureInfo.InvariantCulture)!.Split(':');
            if (parts.Length != 3)
                throw new InvalidOperationException("Incomplete physical PostgreSQL namespace identity.");
            schemaOid = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            if (parts[1].Length > 0)
            {
                if (parts[2] != "r")
                    throw new InvalidOperationException("Target is not an ordinary PostgreSQL physical table.");
                oid = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var columns = new List<DdlPhysicalColumn>();
        var indexes = new List<DdlPhysicalIndex>();
        if (oid.HasValue)
        {
            await using (var command = Command(connection, transaction,
                "SELECT a.attname::text, pg_catalog.format_type(a.atttypid, a.atttypmod), "
                + "NOT a.attnotnull FROM pg_catalog.pg_attribute a "
                + "WHERE a.attrelid = @oid::oid AND a.attnum > 0 AND NOT a.attisdropped "
                + "ORDER BY a.attnum", ("oid", oid.Value)))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    columns.Add(new DdlPhysicalColumn(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            }

            await using (var command = Command(connection, transaction,
                "SELECT ci.relname::text, ix.indisunique, ix.indpred IS NOT NULL, ix.indexprs IS NOT NULL, "
                + "COALESCE(string_agg(a.attname::text, ',' ORDER BY keys.n), '') "
                + "FROM pg_catalog.pg_index ix "
                + "JOIN pg_catalog.pg_class ci ON ci.oid = ix.indexrelid "
                + "LEFT JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS keys(attnum,n) "
                + "ON keys.n <= ix.indnkeyatts "
                + "LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid = ix.indrelid "
                + "AND a.attnum = keys.attnum "
                + "WHERE ix.indrelid = @oid::oid "
                + "GROUP BY ci.relname, ix.indisunique, ix.indpred, ix.indexprs "
                + "ORDER BY ci.relname", ("oid", oid.Value)))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    indexes.Add(new DdlPhysicalIndex(
                        reader.GetString(0),
                        reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries),
                        reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3)));
            }
        }

        var normalized = JsonSerializer.Serialize(new
        {
            identity.ServerIdentity, identity.DatabaseName, schema, schemaOid, table, oid,
            columns, indexes
        });
        return new DdlPhysicalSnapshot(identity.ServerIdentity, identity.DatabaseName,
            schema, schemaOid, table, oid, columns.AsReadOnly(), indexes.AsReadOnly(), Digest(normalized));
    }

    private static async Task<(string ServerIdentity, string DatabaseName)> ReadIdentityAsync(
        DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT current_database()::text, "
            + "(SELECT oid::text FROM pg_catalog.pg_database WHERE datname = current_database()), "
            + "current_user::text, inet_server_addr()::text, inet_server_port(), "
            + "pg_is_in_recovery(), current_setting('server_version_num')::text, "
            + "(pg_catalog.pg_control_system()).system_identifier::text");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.IsDBNull(1) || reader.IsDBNull(3) || reader.IsDBNull(4)
            || reader.IsDBNull(7) || reader.GetBoolean(5))
            throw new InvalidOperationException("Physical PostgreSQL server identity is unavailable or server is read-only.");
        var databaseName = reader.GetString(0);
        var identity = string.Join("|", reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt32(4).ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(6), reader.GetString(7));
        // Opaque 256-bit binding: do not expose server address, login or
        // PostgreSQL cluster system identifier in MCP preview responses.
        return (Digest(identity), databaseName);
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection, DbTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteHostSqlAsync(
        DbConnection connection, DbTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand Command(
        DbConnection connection, DbTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.CommandTimeout = 15;
        foreach (var (name, value) in parameters)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            cmd.Parameters.Add(parameter);
        }
        return cmd;
    }
}
