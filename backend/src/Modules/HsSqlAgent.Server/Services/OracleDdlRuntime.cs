using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using Oracle.ManagedDataAccess.Client;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Oracle 26ai single CREATE TABLE implicit-commit execution proof. No user
/// transaction or rollback is possible for Oracle DDL. DBMS_LOCK coordinates
/// trusted Host sessions only and is NOT a CAS against external DDL writers.
/// </summary>
public sealed class OracleDdlRuntime(
    OracleDdlExecutionOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly OracleDdlExecutionOptions _options = options ?? new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // Only stable proof stages and Oracle numeric codes leave this boundary;
    // never surface driver messages, SQL, connection strings, DBIDs or GUIDs.
    private sealed class PhysicalProofFailure(string stage, string kind, Exception inner)
        : Exception("Oracle metadata stage not proven.", inner)
    {
        public string Stage { get; } = stage;
        public string Kind { get; } = kind;
    }
    public bool Enabled => _options.Enabled;

    public async Task<OracleDdlRuntimeResult> RunAsync(
        ISqlProvider provider, string connectionString, string sql,
        DdlActorContext actor, bool previewOnly,
        IDdlApprovalProvider? approvalProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (System.Transactions.Transaction.Current is not null)
            return Deny("ddl.ambient_transaction_denied",
                "Oracle DDL cannot execute under an ambient transaction.");
        if (!_options.Enabled)
            return Deny("ddl.disabled", "Oracle DDL is disabled by default.");
        if (provider.Type != SqlAgentToolType.Oracle)
            return Deny("ddl.provider_denied", "Oracle-only DDL adapter.");
        if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(connectionString)
            || actor is null || string.IsNullOrWhiteSpace(actor.DatabaseName)
            || string.IsNullOrWhiteSpace(actor.PrincipalIdentity)
            || string.IsNullOrWhiteSpace(actor.TargetIdentity))
            return Deny("ddl.context_missing", "Authenticated Oracle actor and target are required.");
        if (!previewOnly && approvalProvider is null)
            return Deny("ddl.approval_required", "Separate Oracle DDL approval is required.");

        CompiledDdlPlan plan;
        string table;
        try
        {
            ValidateOptions();
            plan = Compile(sql, null);
            var change = plan.Changes[0];
            if (change.ObjectIdentity is null || change.ObjectIdentity.Parts.Count != 1)
                return Deny("ddl.unqualified_identity_required",
                    "Oracle G5 executes only one object in the verified current user schema.");
            table = change.ObjectIdentity.Parts[0];
            if (!_options.AllowCreateTable || change.Operation != DdlSchemaOperation.CreateTable)
                return Deny("ddl.operation_denied", "Only CREATE TABLE is proven for Oracle.");
            if (change.ColumnDefinitions.Count is < 1 or > 16
                || change.ColumnDefinitions.Any(c =>
                    !string.Equals(c.TypeName, "INTEGER", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(c.TypeName, "INT", StringComparison.OrdinalIgnoreCase)))
                return Deny("ddl.type_denied", "Only 1–16 Oracle INTEGER columns are proven.");
            if (plan.Command.Parameters.Count != 0
                || plan.TransactionBehavior != DdlTransactionBehavior.ImplicitCommit)
                return Deny("ddl.transaction_denied", "Only implicit-commit DDL is proven.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Deny("ddl.compile_denied", "SQL is outside the proven Oracle DDL grammar.");
        }

        try
        {
            OracleDdlPhysicalSnapshot before;
            await using (var connection = provider.Connections.Create(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                before = await SnapshotAsync(connection, table, cancellationToken);
                if (before.PdbName != actor.DatabaseName
                    || !_options.AllowedPdbNames.Contains(before.PdbName)
                    || !_options.AllowedSchemas.Contains(before.SchemaName))
                    return Deny("ddl.identity_denied",
                        "Oracle target PDB and session schema must be explicitly allowlisted.");
                if (before.ObjectExists)
                    return Deny("ddl.physical_precondition",
                        "A physical Oracle object already uses this name.");
            }

            // Version 23.26 (marketed as 26ai) was observed directly in the
            // privileged physical snapshot. The F# frontend owns every grammar decision.
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.Oracle, new Version(23, 26));
            plan = Compile(sql, profile);
            var staticPreview = SqlCoreFacade.PreviewDdl(sql,
                new DdlCompilationOptions(SqlAgentToolType.Oracle,
                    SqlAgentToolType.Oracle, profile, profile));
            var expected = plan.Changes[0].ColumnDefinitions
                .Select(c => new DdlPhysicalColumn(c.Name, "NUMBER(38,0)", c.Nullable))
                .ToArray();
            var issued = _time.GetUtcNow();
            var expires = issued + _options.ApprovalLifetime;
            var requestId = "ddl_" + Guid.NewGuid().ToString("N");
            var policy = PolicyMaterial();
            var summary = "Oracle 26ai CREATE TABLE " + before.SchemaName + "." + table
                + "\nPDB: " + before.PdbName
                + "\nBefore: absent\nExpected columns: "
                + string.Join(", ", expected.Select(c => c.Name + " INTEGER "
                    + (c.Nullable ? "NULL" : "NOT NULL")))
                + "\nRisk: Oracle implicitly commits before and after DDL; "
                + "the change CANNOT be rolled back by the application. "
                + "Object lock duration and affected rows: unknown.";
            var fingerprint = Digest(JsonSerializer.Serialize(new
            {
                domain = "oracle26ai-ddl-v1", plan.PlanFingerprint,
                nativeSql = plan.Command.Sql, before.PhysicalIdentity,
                before.Fingerprint, actor.PrincipalIdentity, actor.TargetIdentity,
                actor.DatabaseName, policy, requestId,
                issued = issued.ToUnixTimeMilliseconds(),
                expires = expires.ToUnixTimeMilliseconds(), summary
            }));
            var preview = new OracleDdlRuntimePreview(staticPreview, before,
                Array.AsReadOnly(expected), fingerprint, expires, requestId, summary);
            if (previewOnly)
                return new(DdlRuntimeStatus.Preview, preview,
                    "Read-only verified Oracle PDB preview; no DDL executed.");

            DdlApprovalDecision decision;
            try
            {
                decision = await approvalProvider!.RequestAsync(
                    new DdlApprovalRequest(requestId, fingerprint, plan.PlanFingerprint,
                        actor.PrincipalIdentity, actor.TargetIdentity,
                        before.PhysicalIdentity, _options.PolicyRevision,
                        issued, expires, summary), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Deny("ddl.approval_cancelled", "Oracle DDL approval cancelled.", preview);
            }
            catch (Exception)
            {
                return Deny("ddl.approval_provider_failed",
                    "Oracle independent DDL approval failed.", preview);
            }
            if (decision is null
                || !string.Equals(decision.RequestId, requestId, StringComparison.Ordinal)
                || !string.Equals(decision.ApprovalFingerprint, fingerprint, StringComparison.Ordinal))
                return Deny("ddl.approval_mismatch", "Oracle DDL approval mismatch.", preview);
            if (decision.State == DdlApprovalState.Pending)
                return new(DdlRuntimeStatus.Pending, preview,
                    "Pending DDL approval is never resumable; request a new preview.");
            if (decision.State != DdlApprovalState.Approved
                || string.IsNullOrWhiteSpace(decision.ApproverIdentity)
                || string.Equals(decision.ApproverIdentity, actor.PrincipalIdentity,
                    StringComparison.Ordinal))
                return Deny("ddl.approval_denied", "Oracle DDL approval denied.", preview);
            if (_time.GetUtcNow() >= expires || policy != PolicyMaterial())
                return Deny("ddl.policy_changed", "Oracle DDL approval expired or policy changed.", preview);

            return await ExecuteOnceAsync(provider, connectionString, plan, preview,
                policy, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Deny("ddl.cancelled_before_execution",
                "Oracle DDL cancelled before native execution.");
        }
        catch (PhysicalProofFailure ex)
        {
            return Deny("ddl.preview_failed",
                "Oracle PDB identity or metadata unproven (stage: " + ex.Stage
                + ", kind: " + ex.Kind + ").");
        }
        catch (Exception)
        {
            return Deny("ddl.preview_failed",
                "Oracle PDB identity, DDL trigger visibility or schema metadata unproven.");
        }
    }

    private async Task<OracleDdlRuntimeResult> ExecuteOnceAsync(
        ISqlProvider provider, string connectionString, CompiledDdlPlan plan,
        OracleDdlRuntimePreview preview, string approvedPolicy,
        CancellationToken token)
    {
        DbConnection? connection = null;
        bool beganNative = false, startedRecheck = false;
        // Numeric session-scoped DBMS_LOCK survives Oracle's implicit commits
        // via release_on_commit => FALSE. It serializes only cooperating Hosts.
        var lockId = (int)(Convert.ToUInt32(
            Digest(preview.Before.PhysicalIdentity + "|" + preview.Before.TableName)[..8],
            16) % 1073741823u);
        bool locked = false;
        try
        {
            connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(token);
            var lockResult = await OracleLockAsync(connection, lockId, false, token);
            if (lockResult != 0)
                return Deny("ddl.lock_denied",
                    "Oracle cooperative session lock was not acquired.", preview);
            locked = true;
            startedRecheck = true;
            var fresh = await SnapshotAsync(connection, preview.Before.TableName, token);
            if (fresh.Fingerprint != preview.Before.Fingerprint
                || fresh.PhysicalIdentity != preview.Before.PhysicalIdentity
                || fresh.ObjectExists || _time.GetUtcNow() >= preview.ExpiresAt
                || approvedPolicy != PolicyMaterial())
                return Deny("ddl.schema_drift",
                    "Oracle PDB, schema, approval, policy or target changed.", preview);

            // This command alone crosses the irreversible Oracle DDL boundary.
            // The execution connection has no ambient or explicit transaction.
            beganNative = true;
            await using (var command = Command(connection, plan.Command.Sql))
                await command.ExecuteNonQueryAsync(token);

            // Postcondition verification cannot undo a CREATE which has
            // already implicitly committed. Failures are OutcomeUnknown.
            var after = await SnapshotAsync(connection, preview.Before.TableName, token);
            if (after.PhysicalIdentity != preview.Before.PhysicalIdentity
                || !after.ObjectExists
                || after.Columns.Count != preview.ExpectedColumns.Count
                || !after.Columns.Zip(preview.ExpectedColumns).All(pair =>
                    string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                    && string.Equals(pair.First.TypeName, pair.Second.TypeName,
                        StringComparison.Ordinal)
                    && pair.First.Nullable == pair.Second.Nullable))
                return Unknown(preview,
                    "Oracle DDL returned but the exact physical postcondition is unproven.");

            return new(DdlRuntimeStatus.Committed, preview,
                "Oracle CREATE TABLE implicitly committed and physical metadata was verified. "
                + "Rollback is not available.");
        }
        catch (Exception ex)
        {
            if (beganNative)
            {
                var proofKind = ex switch
                {
                    PhysicalProofFailure proof => "metadata/" + proof.Stage + "/" + proof.Kind,
                    OracleException ora => "oracle/ORA-" + ora.Number.ToString(CultureInfo.InvariantCulture),
                    _ => "runtime/" + ex.GetType().Name
                };
                return Unknown(preview,
                    "Oracle DDL may have committed despite a lost reply or metadata failure ("
                    + proofKind + ").");
            }
            return Deny(startedRecheck ? "ddl.schema_drift" : "ddl.pre_execution_failed",
                "Oracle metadata or permission could not be proven before native DDL.", preview);
        }
        finally
        {
            if (connection is not null)
            {
                if (locked)
                    try { await OracleLockAsync(connection, lockId, true, CancellationToken.None); }
                    catch (Exception) { }
                try { await connection.DisposeAsync(); } catch (Exception) { }
            }
        }
    }

    private async Task<OracleDdlPhysicalSnapshot> SnapshotAsync(
        DbConnection connection, string table, CancellationToken token)
    {
        var stage = "session";
        try
        {
        string dbName, dbUnique, pdb, schema, role, service, version;
        string cdbId, pdbId, guid;
        await using (var command = Command(connection,
            "SELECT SYS_CONTEXT('USERENV','DB_NAME'), "
            + "SYS_CONTEXT('USERENV','DB_UNIQUE_NAME'), "
            + "SYS_CONTEXT('USERENV','CON_NAME'), "
            + "SYS_CONTEXT('USERENV','SESSION_USER'), "
            + "SYS_CONTEXT('USERENV','CURRENT_SCHEMA'), "
            + "SYS_CONTEXT('USERENV','DATABASE_ROLE'), "
            + "SYS_CONTEXT('USERENV','SERVICE_NAME') FROM DUAL"))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)
                || Enumerable.Range(0, 7).Any(reader.IsDBNull))
                throw new InvalidOperationException("Oracle session identity is missing.");
            dbName = reader.GetString(0); dbUnique = reader.GetString(1);
            pdb = reader.GetString(2); schema = reader.GetString(3);
            var currentSchema = reader.GetString(4); role = reader.GetString(5);
            service = reader.GetString(6);
            if (string.IsNullOrWhiteSpace(dbName) || string.IsNullOrWhiteSpace(dbUnique)
                || string.IsNullOrWhiteSpace(pdb) || string.IsNullOrWhiteSpace(service)
                || !string.Equals(currentSchema, schema, StringComparison.Ordinal)
                || schema is "SYS" or "SYSTEM" or "PUBLIC"
                || pdb is "CDB$ROOT" or "PDB$SEED"
                || role != "PRIMARY")
                throw new InvalidOperationException("Oracle PDB, schema or role is not proven.");
        }

        stage = "version";
        // The sole supported executable version is Oracle Database 23.26
        // (marketed as 26ai), not Oracle 19c or an arbitrary compatible dialect.
        await using (var command = Command(connection,
            "SELECT VERSION_FULL FROM PRODUCT_COMPONENT_VERSION "
            + "WHERE (PRODUCT LIKE 'Oracle Database%' " +
                "OR PRODUCT LIKE 'Oracle AI Database%') AND VERSION_FULL IS NOT NULL"))
        {
            var raw = await command.ExecuteScalarAsync(token);
            version = raw?.ToString()?.Trim() ?? "";
            if (!IsProvenOracle26Ai(version))
                throw new InvalidOperationException("Oracle 26ai engine version proof missing.");
        }

        stage = "cdb";
        await using (var command = Command(connection,
            "SELECT TO_CHAR(DBID) FROM V$DATABASE"))
        {
            var raw = await command.ExecuteScalarAsync(token);
            cdbId = raw?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(cdbId))
                throw new InvalidOperationException("Oracle root database DBID unproven.");
        }
        stage = "pdb";
        await using (var command = Command(connection,
            "SELECT TO_CHAR(DBID), RAWTOHEX(GUID), OPEN_MODE FROM V$PDBS "
            + "WHERE CON_ID=TO_NUMBER(SYS_CONTEXT('USERENV','CON_ID'))"))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token) || reader.IsDBNull(0)
                || reader.IsDBNull(1) || reader.IsDBNull(2)
                || reader.GetString(2) != "READ WRITE")
                throw new InvalidOperationException("Writable Oracle PDB DBID/GUID missing.");
            pdbId = reader.GetString(0).Trim();
            guid = reader.GetString(1).Trim();
            if (string.IsNullOrWhiteSpace(pdbId) || guid.Length != 32
                || await reader.ReadAsync(token))
                throw new InvalidOperationException("Ambiguous Oracle PDB identity.");
        }

        stage = "ddl-triggers";
        // Missing dictionary grants must cause a hard failure; ALL_TRIGGERS
        // does not prove absence of database-scoped triggers owned by others.
        await using (var command = Command(connection,
            "SELECT COUNT(*) FROM DBA_TRIGGERS WHERE STATUS = 'ENABLED' "
            + "AND (BASE_OBJECT_TYPE IN ('DATABASE','PLUGGABLE DATABASE') "
            + "OR (BASE_OBJECT_TYPE = 'SCHEMA' AND OWNER = :owner))",
            (":owner", schema)))
        {
            var count = await command.ExecuteScalarAsync(token);
            if (count is null || count is DBNull
                || Convert.ToInt32(count, CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Enabled Oracle DDL triggers are unsupported.");
        }

        stage = "objects";
        bool exists = false;
        await using (var command = Command(connection,
            "SELECT OBJECT_TYPE FROM USER_OBJECTS WHERE OBJECT_NAME=:object_name",
            (":object_name", table)))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (await reader.ReadAsync(token))
            {
                if (reader.IsDBNull(0) || reader.GetString(0) != "TABLE"
                    || await reader.ReadAsync(token))
                    throw new InvalidOperationException("Oracle object name is not an ordinary table.");
                exists = true;
            }
        }

        stage = "columns";
        var columns = new List<DdlPhysicalColumn>();
        if (exists)
        {
            await using (var command = Command(connection,
                "SELECT TEMPORARY, NESTED, IOT_TYPE, CLUSTER_NAME, PARTITIONED "
                + "FROM USER_TABLES WHERE TABLE_NAME=:object_name", (":object_name", table)))
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token) || reader.GetString(0) != "N"
                    || reader.GetString(1) != "NO" || !reader.IsDBNull(2)
                    || !reader.IsDBNull(3) || reader.GetString(4) != "NO"
                    || await reader.ReadAsync(token))
                    throw new InvalidOperationException("Unsupported Oracle physical table shape.");
            }
            await using (var command = Command(connection,
                "SELECT CONSTRAINT_TYPE, GENERATED FROM USER_CONSTRAINTS "
                + "WHERE TABLE_NAME=:object_name", (":object_name", table)))
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                    if (reader.GetString(0) != "C" || reader.GetString(1) != "GENERATED NAME")
                        throw new InvalidOperationException("Explicit Oracle constraints unproven.");
            }
            await using (var command = Command(connection,
                "SELECT COLUMN_NAME, DATA_TYPE, DATA_PRECISION, DATA_SCALE, "
                + "NULLABLE, DEFAULT_LENGTH, IDENTITY_COLUMN, VIRTUAL_COLUMN, HIDDEN_COLUMN "
                + "FROM USER_TAB_COLS WHERE TABLE_NAME=:object_name ORDER BY INTERNAL_COLUMN_ID",
                (":object_name", table)))
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    // Oracle 26ai persists native INTEGER as NUMBER with scale 0;
                    // USER_TAB_COLS may report precision NULL (the live G5 fixture)
                    // or 38. Both are limited to this compiled INTEGER-only shape.
                    if (reader.IsDBNull(0) || reader.IsDBNull(1)
                        || reader.IsDBNull(3)
                        || reader.IsDBNull(4) || reader.IsDBNull(6)
                        || reader.IsDBNull(7) || reader.IsDBNull(8)
                        || columns.Count == 16
                        || reader.GetString(1) != "NUMBER"
                        || (!reader.IsDBNull(2)
                            && Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 38)
                        || Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) != 0
                        || !reader.IsDBNull(5)
                        || reader.GetString(6) != "NO"
                        || reader.GetString(7) != "NO"
                        || reader.GetString(8) != "NO")
                        throw new InvalidOperationException("Unsupported Oracle column semantics.");
                    columns.Add(new DdlPhysicalColumn(reader.GetString(0),
                        "NUMBER(38,0)", reader.GetString(4) == "Y"));
                }
            }
        }
        stage = "fingerprint";
        var physical = Digest(JsonSerializer.Serialize(new
        {
            dbName, dbUnique, pdb, schema, role, service, version, cdbId, pdbId, guid
        }));
        var fp = Digest(JsonSerializer.Serialize(new { physical, table, exists, columns }));
        return new(physical, pdb, schema, table, Digest(schema), exists,
            columns.AsReadOnly(), fp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var kind = ex is OracleException ora ? "ORA-" + ora.Number.ToString(CultureInfo.InvariantCulture)
                : ex.GetType().Name;
            throw new PhysicalProofFailure(stage, kind, ex);
        }
    }

    internal static bool IsProvenOracle26Ai(string? fullVersion)
    {
        if (string.IsNullOrWhiteSpace(fullVersion)) return false;
        var parts = fullVersion.Split('.');
        return parts.Length == 5
            && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && major == 23 && minor >= 26;
    }

    private async Task<int> OracleLockAsync(
        DbConnection connection, int lockId, bool release, CancellationToken token)
    {
        // DBMS_LOCK named args are explicitly bound by name; release_on_commit
        // FALSE prevents Oracle's implicit DDL commits from freeing the Host lock.
        await using var command = Command(connection, release
            ? "BEGIN :answer := DBMS_LOCK.RELEASE(:lockid); END;"
            : "BEGIN :answer := DBMS_LOCK.REQUEST(:lockid, 6, 3, FALSE); END;");
        var answer = command.CreateParameter();
        answer.ParameterName = "answer";
        answer.Direction = ParameterDirection.Output;
        answer.DbType = DbType.Decimal;
        command.Parameters.Add(answer);
        var id = command.CreateParameter();
        id.ParameterName = "lockid";
        id.DbType = DbType.Int32;
        id.Value = lockId;
        command.Parameters.Add(id);
        await command.ExecuteNonQueryAsync(token);
        return Convert.ToInt32(answer.Value.ToString(), CultureInfo.InvariantCulture);
    }

    private void ValidateOptions()
    {
        if (!_options.AllowCreateTable || _options.AllowedSchemas.Count == 0
            || _options.AllowedPdbNames.Count == 0
            || string.IsNullOrWhiteSpace(_options.PolicyRevision)
            || _options.ApprovalLifetime <= TimeSpan.Zero
            || _options.ApprovalLifetime > TimeSpan.FromMinutes(5)
            || _options.ExecutionTimeout <= TimeSpan.Zero
            || _options.ExecutionTimeout > TimeSpan.FromSeconds(15))
            throw new InvalidOperationException("Oracle implicit-commit opt-in is incomplete.");
    }

    private string PolicyMaterial() => Digest(JsonSerializer.Serialize(new
    {
        _options.Enabled, _options.AllowCreateTable, _options.PolicyRevision,
        schemas = _options.AllowedSchemas.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        pdbs = _options.AllowedPdbNames.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        _options.ApprovalLifetime, _options.ExecutionTimeout
    }));

    private static CompiledDdlPlan Compile(string sql, SqlProviderCapabilityProfile? version) =>
        SqlCoreFacade.CompileDdl(sql, version is null
            ? new DdlCompilationOptions(SqlAgentToolType.Oracle, SqlAgentToolType.Oracle)
            : new DdlCompilationOptions(SqlAgentToolType.Oracle, SqlAgentToolType.Oracle,
                version, version));

    private DbCommand Command(DbConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        if (command is OracleCommand oracle) oracle.BindByName = true;
        command.CommandText = sql;
        command.CommandTimeout = Math.Clamp(
            (int)Math.Ceiling(_options.ExecutionTimeout.TotalSeconds), 1, 15);
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name.TrimStart(':');
            p.Value = value;
            command.Parameters.Add(p);
        }
        return command;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static OracleDdlRuntimeResult Deny(string code, string message,
        OracleDdlRuntimePreview? preview = null) =>
        new(DdlRuntimeStatus.Rejected, preview, message, code);

    private static OracleDdlRuntimeResult Unknown(
        OracleDdlRuntimePreview preview, string message) =>
        new(DdlRuntimeStatus.OutcomeUnknown, preview,
            message + " Never retry automatically; reconcile actual Oracle PDB metadata.",
            "ddl.outcome_unknown");
}
