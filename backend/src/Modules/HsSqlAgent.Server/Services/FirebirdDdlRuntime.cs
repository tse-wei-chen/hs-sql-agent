using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// One bounded Firebird 5 transactional CREATE TABLE proof. No SqlCore driver,
/// duplicate SQL parser, DML approval or raw SQL execution is introduced.
/// Firebird metadata locking is NOT claimed to be an external global CAS.
/// </summary>
public sealed class FirebirdDdlRuntime(
    FirebirdDdlExecutionOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly FirebirdDdlExecutionOptions _options = options ?? new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public bool Enabled => _options.Enabled;

    public async Task<FirebirdDdlRuntimeResult> RunAsync(
        ISqlProvider provider, string connectionString, string sql,
        DdlActorContext actor, bool previewOnly,
        IDdlApprovalProvider? approvalProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_options.Enabled)
            return Deny("ddl.disabled", "Firebird DDL is disabled by default.");
        if (provider.Type != SqlAgentToolType.Firebird)
            return Deny("ddl.provider_denied", "Firebird-only DDL adapter.");
        if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(connectionString)
            || actor is null || string.IsNullOrWhiteSpace(actor.DatabaseName)
            || string.IsNullOrWhiteSpace(actor.PrincipalIdentity)
            || string.IsNullOrWhiteSpace(actor.TargetIdentity))
            return Deny("ddl.context_missing", "Authenticated actor and target are required.");
        if (!previewOnly && approvalProvider is null)
            return Deny("ddl.approval_required", "An independent DDL approval provider is required.");

        CompiledDdlPlan plan;
        string table;
        try
        {
            ValidateOptions();
            plan = Compile(sql, null);
            var change = plan.Changes[0];
            // Firebird has no ordinary per-table SQL schema qualifier.
            if (change.ObjectIdentity is null || change.ObjectIdentity.Parts.Count != 1)
                return Deny("ddl.unqualified_identity_required",
                    "Firebird requires one unqualified physical table identifier.");
            table = change.ObjectIdentity.Parts[0];
            if (change.Operation != DdlSchemaOperation.CreateTable || !_options.AllowCreateTable)
                return Deny("ddl.operation_denied", "Only CREATE TABLE is proven for Firebird.");
            if (change.ColumnDefinitions.Count is < 1 or > 16
                || change.ColumnDefinitions.Any(c =>
                    !string.Equals(c.TypeName, "INT", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(c.TypeName, "INTEGER", StringComparison.OrdinalIgnoreCase)))
                return Deny("ddl.type_denied", "Only 1–16 simple INT columns are proven.");
            if (plan.Command.Parameters.Count != 0)
                return Deny("ddl.parameters_denied", "DDL runtime parameters are unsupported.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Deny("ddl.compile_denied", "SQL is outside the proven Firebird DDL subset.");
        }

        try
        {
            // A read-only preview uses a disposable connection, never held across human approval.
            FirebirdDdlPhysicalSnapshot before;
            string observedGuid;
            await using (var connection = provider.Connections.Create(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                (before, observedGuid) = await SnapshotAsync(
                    connection, null, table, cancellationToken);
                if (!_options.AllowedDatabaseGuids.Contains(observedGuid))
                    return Deny("ddl.database_identity_denied",
                        "Firebird physical GUID is not in the explicit allowlist.");
                if (before.TableExists)
                    return Deny("ddl.physical_precondition", "Target Firebird relation already exists.");
            }

            // The observed live version is Firebird 5. The F# compiler still owns all
            // grammar, typed plan and provider-native rendering decisions.
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.Firebird, new Version(5, 0));
            plan = Compile(sql, profile);
            var staticPreview = SqlCoreFacade.PreviewDdl(sql,
                new DdlCompilationOptions(SqlAgentToolType.Firebird,
                    SqlAgentToolType.Firebird, profile, profile));
            var expected = plan.Changes[0].ColumnDefinitions
                .Select(c => new DdlPhysicalColumn(c.Name, "INTEGER", c.Nullable)).ToArray();
            var now = _time.GetUtcNow();
            var expiry = now + _options.ApprovalLifetime;
            var nonce = "ddl_" + Guid.NewGuid().ToString("N");
            var policy = PolicyMaterial();
            var readable = "Firebird 5 CREATE TABLE " + table
                + "\nBefore: absent\nExpected columns: "
                + string.Join(", ", expected.Select(c => c.Name + " INTEGER "
                    + (c.Nullable ? "NULL" : "NOT NULL")))
                + "\nReal metadata will be verified before and after the native statement."
                + " External metadata locking, affected rows and duration are not guaranteed.";
            var fingerprint = Digest(JsonSerializer.Serialize(new
            {
                version = "firebird-ddl-v1", plan.PlanFingerprint,
                plan.Command.Sql, before.DatabaseGuidFingerprint,
                before.PhysicalIdentity, before.Fingerprint,
                actor.PrincipalIdentity, actor.TargetIdentity, actor.DatabaseName,
                policy, nonce, issued = now.ToUnixTimeMilliseconds(),
                expires = expiry.ToUnixTimeMilliseconds(), readable
            }));
            var preview = new FirebirdDdlRuntimePreview(
                staticPreview, before, Array.AsReadOnly(expected),
                fingerprint, expiry, nonce, readable);
            if (previewOnly)
                return new(DdlRuntimeStatus.Preview, preview,
                    "Read-only physical Firebird 5 preview. No DDL executed.");

            DdlApprovalDecision decision;
            try
            {
                decision = await approvalProvider!.RequestAsync(
                    new DdlApprovalRequest(nonce, fingerprint, plan.PlanFingerprint,
                        actor.PrincipalIdentity, actor.TargetIdentity, before.PhysicalIdentity,
                        _options.PolicyRevision, now, expiry, readable), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Deny("ddl.approval_cancelled", "DDL approval cancelled.", preview);
            }
            catch (Exception)
            {
                return Deny("ddl.approval_provider_failed",
                    "Independent Firebird DDL approval failed.", preview);
            }
            if (decision is null
                || !string.Equals(decision.RequestId, nonce, StringComparison.Ordinal)
                || !string.Equals(decision.ApprovalFingerprint, fingerprint, StringComparison.Ordinal))
                return Deny("ddl.approval_mismatch", "DDL approval fingerprint mismatch.", preview);
            if (decision.State == DdlApprovalState.Pending)
                return new(DdlRuntimeStatus.Pending, preview,
                    "Pending DDL approval is not resumable; issue a new preview.");
            if (decision.State != DdlApprovalState.Approved
                || string.IsNullOrWhiteSpace(decision.ApproverIdentity)
                || string.Equals(decision.ApproverIdentity, actor.PrincipalIdentity,
                    StringComparison.Ordinal))
                return Deny("ddl.approval_denied", "Independent DDL approval denied.", preview);
            if (_time.GetUtcNow() >= expiry || policy != PolicyMaterial())
                return Deny("ddl.policy_changed", "Approval expired or DDL policy changed.", preview);

            return await CommitOnceAsync(provider, connectionString, plan, preview,
                observedGuid, policy, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Deny("ddl.cancelled_before_execution",
                "Firebird DDL was cancelled before native execution.");
        }
        catch (Exception)
        {
            return Deny("ddl.preview_failed",
                "Firebird metadata identity or permissions could not be proven.");
        }
    }

    private async Task<FirebirdDdlRuntimeResult> CommitOnceAsync(
        ISqlProvider provider, string connectionString, CompiledDdlPlan plan,
        FirebirdDdlRuntimePreview preview, string approvedGuid,
        string approvedPolicy, CancellationToken token)
    {
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        bool startedNative = false, startedCommit = false;
        try
        {
            // Firebird's transaction semantics and DDL triggers are checked on
            // the actual write attachment. This is not PostgreSQL advisory-lock proof.
            connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(token);
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable, token);
            var (fresh, guid) = await SnapshotAsync(connection, transaction,
                preview.Before.TableName, token);
            if (!string.Equals(guid, approvedGuid, StringComparison.OrdinalIgnoreCase)
                || fresh.Fingerprint != preview.Before.Fingerprint
                || fresh.PhysicalIdentity != preview.Before.PhysicalIdentity
                || fresh.TableExists || _time.GetUtcNow() >= preview.ExpiresAt
                || approvedPolicy != PolicyMaterial())
                return await RollbackAsync(transaction, preview, false,
                    "ddl.schema_drift", "Firebird physical identity, schema or policy changed.");

            // Only typed, native SQL emitted by SqlCore is executable.
            startedNative = true;
            await using (var command = Command(connection, transaction, plan.Command.Sql))
                await command.ExecuteNonQueryAsync(token);
            var (after, afterGuid) = await SnapshotAsync(connection, transaction,
                preview.Before.TableName, token);
            var correct = string.Equals(afterGuid, approvedGuid,
                    StringComparison.OrdinalIgnoreCase)
                && after.PhysicalIdentity == preview.Before.PhysicalIdentity
                && after.TableExists
                && after.Columns.Count == preview.ExpectedColumns.Count
                && after.Columns.Zip(preview.ExpectedColumns).All(pair =>
                    string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                    && string.Equals(pair.First.TypeName, pair.Second.TypeName,
                        StringComparison.OrdinalIgnoreCase)
                    && pair.First.Nullable == pair.Second.Nullable);
            if (!correct)
                return await RollbackAsync(transaction, preview, true,
                    "ddl.postcondition_mismatch",
                    "Firebird physical column postcondition is unproven.");

            // Firebird DDL triggers can run at COMMIT; our trigger visibility
            // requirement and explicit rollback/unknown state must remain intact.
            startedCommit = true;
            await transaction.CommitAsync(token);
            return new(DdlRuntimeStatus.Committed, preview,
                "Firebird CREATE TABLE committed after physical postcondition verification.");
        }
        catch (Exception)
        {
            if (startedCommit)
                return Unknown(preview, "Firebird COMMIT outcome is unconfirmed.");
            if (transaction is not null)
                return await RollbackAsync(transaction, preview, startedNative,
                    startedNative ? "ddl.native_execution_rolled_back" : "ddl.commit_precondition",
                    "Firebird DDL failed before COMMIT.");
            return Deny("ddl.connection_failed",
                "Firebird connection failed before the write transaction.", preview);
        }
        finally
        {
            if (transaction is not null)
                try { await transaction.DisposeAsync(); } catch (Exception) { }
            if (connection is not null)
                try { await connection.DisposeAsync(); } catch (Exception) { }
        }
    }

    private static async Task<FirebirdDdlRuntimeResult> RollbackAsync(
        DbTransaction transaction, FirebirdDdlRuntimePreview preview, bool startedNative,
        string code, string message)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(startedNative ? DdlRuntimeStatus.Failed : DdlRuntimeStatus.Rejected,
                preview, message + " Explicit rollback confirmed.", code);
        }
        catch (Exception)
        {
            return Unknown(preview, "Firebird ROLLBACK outcome is unconfirmed.");
        }
    }

    private async Task<(FirebirdDdlPhysicalSnapshot Snapshot, string Guid)> SnapshotAsync(
        DbConnection connection, DbTransaction? transaction,
        string table, CancellationToken token)
    {
        string guid, fileId, dbName, creation, engine, principal;
        await using (var cmd = Command(connection, transaction,
            "SELECT MON$GUID, MON$FILE_ID, MON$DATABASE_NAME, MON$CREATION_DATE, "
            + "MON$SQL_DIALECT, MON$READ_ONLY, CURRENT_USER, "
            + "RDB$GET_CONTEXT('SYSTEM','ENGINE_VERSION') FROM MON$DATABASE"))
        await using (var reader = await cmd.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)
                || Enumerable.Range(0, 8).Any(reader.IsDBNull)
                || Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) != 3
                || Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Firebird live database identity is unproven.");
            guid = reader.GetValue(0).ToString()!.Trim();
            fileId = reader.GetValue(1).ToString()!.Trim();
            dbName = reader.GetValue(2).ToString()!.Trim();
            creation = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "";
            principal = reader.GetValue(6).ToString()!.Trim();
            engine = reader.GetValue(7).ToString()!.Trim();
            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(fileId)
                || string.IsNullOrWhiteSpace(dbName) || string.IsNullOrWhiteSpace(creation)
                || string.IsNullOrWhiteSpace(principal)
                || !Regex.IsMatch(engine, @"(?:^|[^0-9])5\.[0-9]+",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new InvalidOperationException("Firebird 5 GUID/file/version proof is missing.");
        }

        // Refuse active user database triggers, including DDL and commit triggers.
        // The creating principal must be able to read this catalog or we fail closed.
        await using (var cmd = Command(connection, transaction,
            "SELECT COUNT(*) FROM RDB$TRIGGERS "
            + "WHERE RDB$RELATION_NAME IS NULL AND "
            + "COALESCE(RDB$TRIGGER_INACTIVE,0)=0 AND COALESCE(RDB$SYSTEM_FLAG,0)=0"))
        {
            var triggers = await cmd.ExecuteScalarAsync(token);
            if (triggers is null || triggers is DBNull
                || Convert.ToInt64(triggers, CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Active Firebird database/DDL triggers are unsupported.");
        }

        bool exists = false;
        await using (var cmd = Command(connection, transaction,
            "SELECT RDB$RELATION_TYPE, COALESCE(RDB$SYSTEM_FLAG,0), RDB$VIEW_BLR "
            + "FROM RDB$RELATIONS WHERE RDB$RELATION_NAME=@table",
            ("@table", table)))
        await using (var reader = await cmd.ExecuteReaderAsync(token))
        {
            if (await reader.ReadAsync(token))
            {
                if (reader.IsDBNull(0)
                    || Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) != 0
                    || Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) != 0
                    || !reader.IsDBNull(2))
                    throw new InvalidOperationException("Unexpected Firebird physical relation type.");
                exists = true;
            }
        }

        var columns = new List<DdlPhysicalColumn>();
        if (exists)
        {
            await using var cmd = Command(connection, transaction,
                "SELECT TRIM(TRAILING FROM rf.RDB$FIELD_NAME), "
                + "f.RDB$FIELD_TYPE, COALESCE(f.RDB$FIELD_SCALE,0), "
                + "COALESCE(f.RDB$FIELD_SUB_TYPE,0), COALESCE(rf.RDB$NULL_FLAG,0), "
                + "COALESCE(f.RDB$NULL_FLAG,0), rf.RDB$DEFAULT_SOURCE, "
                + "f.RDB$DEFAULT_SOURCE, f.RDB$COMPUTED_SOURCE "
                + "FROM RDB$RELATION_FIELDS rf "
                + "JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME=rf.RDB$FIELD_SOURCE "
                + "WHERE rf.RDB$RELATION_NAME=@table "
                + "ORDER BY rf.RDB$FIELD_POSITION", ("@table", table));
            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (reader.IsDBNull(0)
                    || Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) != 8
                    || Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0
                    || Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) != 0
                    || !reader.IsDBNull(6) || !reader.IsDBNull(7) || !reader.IsDBNull(8)
                    || columns.Count == 16)
                    throw new InvalidOperationException("Unproven Firebird column or default expression.");
                columns.Add(new DdlPhysicalColumn(reader.GetString(0).TrimEnd(), "INTEGER",
                    Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) != 1
                    && Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) != 1));
            }
        }

        // Hash physical file name / identity: it is neither a public API detail
        // nor a portable SQL Server/PostgreSQL object identity.
        var physicalIdentity = Digest(JsonSerializer.Serialize(new
        {
            guid, fileId, dbName, creation, engine, principal
        }));
        var guidHash = Digest(guid);
        var fingerprint = Digest(JsonSerializer.Serialize(new
        {
            physicalIdentity, guidHash, table, exists, columns
        }));
        return (new FirebirdDdlPhysicalSnapshot(guidHash, physicalIdentity,
            Digest(principal), table, exists, columns.AsReadOnly(), fingerprint), guid);
    }

    private void ValidateOptions()
    {
        if (!_options.AllowCreateTable || _options.AllowedDatabaseGuids.Count == 0
            || string.IsNullOrWhiteSpace(_options.PolicyRevision)
            || _options.ApprovalLifetime <= TimeSpan.Zero
            || _options.ApprovalLifetime > TimeSpan.FromMinutes(5)
            || _options.ExecutionTimeout <= TimeSpan.Zero
            || _options.ExecutionTimeout > TimeSpan.FromSeconds(15))
            throw new InvalidOperationException("Firebird DDL opt-in policy is incomplete.");
    }

    private string PolicyMaterial() => Digest(JsonSerializer.Serialize(new
    {
        _options.Enabled, _options.AllowCreateTable, _options.PolicyRevision,
        guids = _options.AllowedDatabaseGuids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
        _options.ApprovalLifetime, _options.ExecutionTimeout
    }));

    private static CompiledDdlPlan Compile(string sql, SqlProviderCapabilityProfile? profile) =>
        SqlCoreFacade.CompileDdl(sql, profile is null
            ? new DdlCompilationOptions(SqlAgentToolType.Firebird, SqlAgentToolType.Firebird)
            : new DdlCompilationOptions(SqlAgentToolType.Firebird, SqlAgentToolType.Firebird,
                profile, profile));

    private DbCommand Command(DbConnection connection, DbTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = Math.Clamp(
            (int)Math.Ceiling(_options.ExecutionTimeout.TotalSeconds), 1, 15);
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static FirebirdDdlRuntimeResult Deny(
        string code, string message, FirebirdDdlRuntimePreview? preview = null) =>
        new(DdlRuntimeStatus.Rejected, preview, message, code);

    private static FirebirdDdlRuntimeResult Unknown(
        FirebirdDdlRuntimePreview preview, string message) =>
        new(DdlRuntimeStatus.OutcomeUnknown, preview,
            message + " Never automatically retry; reconcile physical Firebird metadata.",
            "ddl.outcome_unknown");
}
