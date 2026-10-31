using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// SQL Server 2022-only DDL Host proof for the first, restricted CREATE TABLE
/// slice. Compile / preview never authorize writes; one transaction-held
/// recheck, an independent human approval and a fresh physical identity do.
/// No user SQL is passed to a DbCommand; only the typed native plan is used.
/// </summary>
public sealed class SqlServerDdlRuntime(
    SqlServerDdlExecutionOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly SqlServerDdlExecutionOptions _options = options ?? new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    public bool Enabled => _options.Enabled;

    public async Task<SqlServerDdlRuntimeResult> RunAsync(
        ISqlProvider provider, string connectionString, string sql,
        DdlActorContext actor, bool previewOnly,
        IDdlApprovalProvider? approvalProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_options.Enabled)
            return Deny("ddl.disabled", "SQL Server DDL is disabled by default.");
        if (provider.Type != SqlAgentToolType.MsSqlServer)
            return Deny("ddl.provider_denied", "SQL Server-only DDL adapter.");
        if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(connectionString)
            || actor is null || string.IsNullOrWhiteSpace(actor.DatabaseName)
            || string.IsNullOrWhiteSpace(actor.PrincipalIdentity)
            || string.IsNullOrWhiteSpace(actor.TargetIdentity))
            return Deny("ddl.context_missing", "Verified target and actor are required.");
        if (!previewOnly && approvalProvider is null)
            return Deny("ddl.approval_required", "A separate DDL approval provider is required.");

        CompiledDdlPlan plan;
        string schema, table;
        try
        {
            ValidateOptions();
            plan = Compile(sql, null);
            var change = plan.Changes[0];
            if (change.ObjectIdentity is null || change.ObjectIdentity.Parts.Count != 2)
                return Deny("ddl.qualified_identity_required",
                    "SQL Server live DDL requires explicit schema.table.");
            schema = change.ObjectIdentity.Parts[0];
            table = change.ObjectIdentity.Parts[1];
            if (!_options.AllowedSchemas.Contains(schema)
                || schema.Equals("sys", StringComparison.OrdinalIgnoreCase)
                || schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase))
                return Deny("ddl.schema_denied", "DDL target schema is not allowlisted.");
            if (!_options.AllowCreateTable || change.Operation != DdlSchemaOperation.CreateTable)
                return Deny("ddl.operation_denied", "Only CREATE TABLE is proven for SQL Server.");
            // A deliberately narrow v1 type family. No DEFAULT, IDENTITY,
            // constraints, expressions, arbitrary SQL type tags or extra effects.
            if (change.ColumnDefinitions.Count is < 1 or > 16 ||
                change.ColumnDefinitions.Any(c =>
                    !string.Equals(c.TypeName, "INT", StringComparison.OrdinalIgnoreCase)))
                return Deny("ddl.type_denied", "Only 1–16 simple INT column definitions are proven.");
            if (plan.Command.Parameters.Count != 0)
                return Deny("ddl.unexpected_parameters", "DDL cannot use runtime parameters.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Deny("ddl.compile_denied", e.Message);
        }

        try
        {
            // No long-lived connection/transaction during human interaction.
            SqlServerDdlPhysicalSnapshot initial;
            await using (var connection = provider.Connections.Create(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                initial = await SnapshotAsync(connection, null, schema, table, cancellationToken);
                if (!string.Equals(initial.DatabaseName, actor.DatabaseName, StringComparison.Ordinal)
                    || IsSystemDatabase(initial.DatabaseName))
                    return Deny("ddl.database_identity_mismatch",
                        "DDL requires a verified, non-system SQL Server database.");
                if (initial.ObjectId.HasValue)
                    return Deny("ddl.physical_precondition", "Target SQL Server name already exists.");
                await EnsureNoDdlTriggersAsync(connection, null, cancellationToken);
            }

            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer, new Version(16, 0));
            plan = Compile(sql, profile);
            var staticPreview = SqlCoreFacade.PreviewDdl(sql,
                new DdlCompilationOptions(SqlAgentToolType.MsSqlServer,
                    SqlAgentToolType.MsSqlServer, profile, profile));
            var expected = plan.Changes[0].ColumnDefinitions
                .Select(c => new DdlPhysicalColumn(c.Name, "int", c.Nullable)).ToArray();
            var policy = PolicyMaterial();
            var now = _time.GetUtcNow();
            var expiry = now + _options.ApprovalLifetime;
            var nonce = "ddl_" + Guid.NewGuid().ToString("N");
            var readable = "SQL Server CREATE TABLE " + schema + "." + table
                + "\nBefore: absent\nExpected columns: "
                + string.Join(", ", expected.Select(c => c.Name + " int "
                    + (c.Nullable ? "NULL" : "NOT NULL")))
                + "\nTransaction held until exact physical postcondition verification. "
                + "Lock duration and affected rows unknown.";
            var fingerprint = Digest(JsonSerializer.Serialize(new
            {
                version = "sqlserver-ddl-v1",
                plan.PlanFingerprint,
                nativeSql = plan.Command.Sql,
                initial.ServerIdentity,
                initial.Fingerprint,
                actor.PrincipalIdentity, actor.TargetIdentity, actor.DatabaseName,
                policy, nonce, issued = now.ToUnixTimeMilliseconds(),
                expires = expiry.ToUnixTimeMilliseconds(), readable
            }));
            var preview = new SqlServerDdlRuntimePreview(staticPreview, initial,
                Array.AsReadOnly(expected), fingerprint, expiry, nonce, readable);
            if (previewOnly)
                return new(DdlRuntimeStatus.Preview, preview,
                    "Read-only SQL Server 2022 physical metadata preview; no DDL executed.");

            DdlApprovalDecision decision;
            try
            {
                decision = await approvalProvider!.RequestAsync(
                    new DdlApprovalRequest(nonce, fingerprint, plan.PlanFingerprint,
                        actor.PrincipalIdentity, actor.TargetIdentity, initial.ServerIdentity,
                        _options.PolicyRevision, now, expiry, readable), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Deny("ddl.approval_cancelled", "DDL approval cancelled.", preview);
            }
            if (decision is null
                || !string.Equals(decision.RequestId, nonce, StringComparison.Ordinal)
                || !string.Equals(decision.ApprovalFingerprint, fingerprint, StringComparison.Ordinal))
                return Deny("ddl.approval_mismatch", "Approval fingerprint mismatch.", preview);
            if (decision.State == DdlApprovalState.Pending)
                return new(DdlRuntimeStatus.Pending, preview,
                    "Pending DDL approval is not resumable; request a fresh preview.");
            if (decision.State != DdlApprovalState.Approved
                || string.IsNullOrWhiteSpace(decision.ApproverIdentity)
                || string.Equals(decision.ApproverIdentity, actor.PrincipalIdentity,
                    StringComparison.Ordinal))
                return Deny("ddl.approval_denied", "Independent DDL approval denied.", preview);
            if (_time.GetUtcNow() >= expiry || policy != PolicyMaterial())
                return Deny("ddl.policy_changed", "DDL approval expired or policy changed.", preview);

            // Only CommitOnce can enter the native SQL execution stage.
            return await CommitOnceAsync(provider, connectionString, plan, preview,
                actor, policy, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // CommitOnce catches all exceptions, including cancelled COMMIT.
            return Deny("ddl.cancelled_before_execution",
                "SQL Server DDL cancelled before execution.");
        }
        catch (Exception)
        {
            return Deny("ddl.preview_failed",
                "SQL Server physical preview or identity proof failed.");
        }
    }

    private async Task<SqlServerDdlRuntimeResult> CommitOnceAsync(
        ISqlProvider provider, string connectionString, CompiledDdlPlan plan,
        SqlServerDdlRuntimePreview preview, DdlActorContext actor,
        string approvedPolicy, CancellationToken token)
    {
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        bool startedNative = false, startedCommit = false;
        var phase = "precondition";
        try
        {
            connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(token);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);

            // Transaction-owned application lock serializes trusted Host requests.
            // It is NOT claimed to lock out unrelated external DDL writers.
            var resource = "hssql-ddl|" + preview.Before.DatabaseId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                + "|" + preview.Before.SchemaName + "|" + preview.Before.TableName;
            var lockResult = await ScalarAsync(connection, transaction,
                "DECLARE @r int; EXEC @r = sys.sp_getapplock "
                + "@Resource=@resource, @LockMode='Exclusive', "
                + "@LockOwner='Transaction', @LockTimeout=3000; SELECT @r;",
                token, ("resource", resource));
            if (Convert.ToInt32(lockResult, System.Globalization.CultureInfo.InvariantCulture) < 0)
                return await RollbackAsync(transaction, preview,
                    "ddl.lock_denied", "SQL Server transaction lock denied.", false);

            var fresh = await SnapshotAsync(connection, transaction,
                preview.Before.SchemaName, preview.Before.TableName, token);
            await EnsureNoDdlTriggersAsync(connection, transaction, token);
            if (fresh.Fingerprint != preview.Before.Fingerprint
                || fresh.ServerIdentity != preview.Before.ServerIdentity
                || fresh.DatabaseId != preview.Before.DatabaseId
                || fresh.DatabaseName != actor.DatabaseName
                || fresh.ObjectId.HasValue
                || _time.GetUtcNow() >= preview.ExpiresAt
                || approvedPolicy != PolicyMaterial())
                return await RollbackAsync(transaction, preview,
                    "ddl.schema_drift", "Target, approval, policy or schema changed.", false);

            startedNative = true;
            phase = "native_execution";
            await ExecuteAsync(connection, transaction, plan.Command.Sql, token);
            phase = "postcondition_verification";
            var after = await SnapshotAsync(connection, transaction,
                preview.Before.SchemaName, preview.Before.TableName, token);
            var correct = after.ObjectId.HasValue
                && after.ServerIdentity == preview.Before.ServerIdentity
                && after.DatabaseId == preview.Before.DatabaseId
                && after.SchemaId == preview.Before.SchemaId
                && after.Columns.Count == preview.ExpectedColumns.Count
                && after.Columns.Zip(preview.ExpectedColumns).All(pair =>
                    pair.First.Name.Equals(pair.Second.Name,
                        StringComparison.OrdinalIgnoreCase)
                    && pair.First.TypeName.Equals(pair.Second.TypeName,
                        StringComparison.OrdinalIgnoreCase)
                    && pair.First.Nullable == pair.Second.Nullable);
            if (!correct)
                return await RollbackAsync(transaction, preview, "ddl.postcondition_mismatch",
                    "SQL Server CREATE TABLE physical postcondition mismatch.", true);
            startedCommit = true;
            await transaction.CommitAsync(token);
            return new(DdlRuntimeStatus.Committed, preview,
                "SQL Server CREATE TABLE committed after physical postcondition verification.");
        }
        catch (Exception)
        {
            if (startedCommit)
                return Unknown(preview, "SQL Server COMMIT outcome is unconfirmed.");
            if (transaction is not null)
                return await RollbackAsync(transaction, preview,
                    !startedNative ? "ddl.commit_precondition"
                        : phase == "native_execution" ? "ddl.native_execution_rolled_back"
                        : "ddl.postcondition_verification_rolled_back",
                    "SQL Server DDL failed within its " + phase + " stage.", startedNative);
            return Deny("ddl.connection_failed",
                "SQL Server connection failed before opening a transaction.", preview);
        }
        finally
        {
            if (transaction is not null)
                try { await transaction.DisposeAsync(); } catch (Exception) { }
            if (connection is not null)
                try { await connection.DisposeAsync(); } catch (Exception) { }
        }
    }

    private static async Task<SqlServerDdlRuntimeResult> RollbackAsync(
        DbTransaction transaction, SqlServerDdlRuntimePreview preview,
        string code, string message, bool startedNative)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(startedNative ? DdlRuntimeStatus.Failed : DdlRuntimeStatus.Rejected,
                preview, message + " Explicit rollback confirmed.", code);
        }
        catch (Exception)
        {
            return Unknown(preview, "SQL Server ROLLBACK outcome is unconfirmed.");
        }
    }

    private static async Task<SqlServerDdlPhysicalSnapshot> SnapshotAsync(
        DbConnection connection, DbTransaction? tx, string schema, string table,
        CancellationToken token)
    {
        await using var identity = Command(connection, tx,
            "SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(128)), "
            + "DB_NAME(), DB_ID(), TRY_CONVERT(int,SERVERPROPERTY('ProductMajorVersion')), "
            + "SUSER_SNAME(), CAST(DATABASEPROPERTYEX(DB_NAME(),'Collation') AS nvarchar(128)), "
            + "SCHEMA_ID(@schema), "
            + "(SELECT o.object_id FROM sys.objects o "
            + "WHERE o.schema_id=SCHEMA_ID(@schema) AND o.name=@table)",
            ("schema", schema), ("table", table));
        await using var reader = await identity.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)
            || Enumerable.Range(0, 7).Any(reader.IsDBNull)
            || reader.GetInt32(3) < 16
            || !reader.GetString(5).Contains("_CI_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQL Server 2022+ identity and CI collation proof required.");
        var serverIdentity = Digest(string.Join("|", reader.GetString(0),
            Convert.ToInt32(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt32(3), reader.GetString(4),
            reader.GetString(5)));
        var database = reader.GetString(1);
        var databaseId = Convert.ToInt32(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);
        var schemaId = Convert.ToInt32(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture);
        int? objectId = reader.IsDBNull(7) ? null
            : Convert.ToInt32(reader.GetValue(7), System.Globalization.CultureInfo.InvariantCulture);
        await reader.DisposeAsync();

        var columns = new List<DdlPhysicalColumn>();
        if (objectId.HasValue)
        {
            await using var columnCommand = Command(connection, tx,
                "SELECT c.name, ty.name, c.is_nullable, c.is_computed, c.default_object_id, o.type "
                + "FROM sys.columns c "
                + "JOIN sys.types ty ON ty.user_type_id=c.user_type_id "
                + "JOIN sys.objects o ON o.object_id=c.object_id "
                + "WHERE c.object_id=@objectId ORDER BY c.column_id",
                ("objectId", objectId.Value));
            await using var columnReader = await columnCommand.ExecuteReaderAsync(token);
            while (await columnReader.ReadAsync(token))
            {
                if (!columnReader.GetString(5).TrimEnd().Equals("U", StringComparison.Ordinal)
                    || columnReader.GetBoolean(3) || columnReader.GetInt32(4) != 0)
                    throw new InvalidOperationException("Unmodeled target object/column effects.");
                columns.Add(new DdlPhysicalColumn(columnReader.GetString(0),
                    columnReader.GetString(1), columnReader.GetBoolean(2)));
                if (columns.Count > 16)
                    throw new InvalidOperationException("Target exceeds proven column bound.");
            }
        }
        var fingerprint = Digest(JsonSerializer.Serialize(new
        {
            serverIdentity, database, databaseId, schema, schemaId, table, objectId, columns
        }));
        return new(serverIdentity, database, databaseId, schemaId,
            schema, table, objectId, columns.AsReadOnly(), fingerprint);
    }

    private static async Task EnsureNoDdlTriggersAsync(
        DbConnection connection, DbTransaction? tx, CancellationToken token)
    {
        // Without complete visibility a trigger may execute arbitrary SQL.
        var authorized = await ScalarAsync(connection, tx,
            "SELECT HAS_PERMS_BY_NAME(NULL,NULL,'VIEW ANY DEFINITION')", token);
        if (authorized is null || authorized is DBNull ||
            Convert.ToInt32(authorized, System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("SQL Server server-wide DDL trigger visibility is unproven.");
        var count = await ScalarAsync(connection, tx,
            "SELECT (SELECT COUNT_BIG(*) FROM sys.triggers "
            + "WHERE parent_class=0 AND is_disabled=0) + "
            + "(SELECT COUNT_BIG(*) FROM sys.server_triggers WHERE is_disabled=0)", token);
        if (count is null || count is DBNull ||
            Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Enabled SQL Server DDL triggers are unsupported.");
    }

    private void ValidateOptions()
    {
        if (_options.AllowedSchemas.Count == 0
            || !_options.AllowCreateTable
            || string.IsNullOrWhiteSpace(_options.PolicyRevision)
            || _options.ApprovalLifetime <= TimeSpan.Zero
            || _options.ApprovalLifetime > TimeSpan.FromMinutes(5)
            || _options.ExecutionTimeout <= TimeSpan.Zero
            || _options.ExecutionTimeout > TimeSpan.FromSeconds(15))
            throw new InvalidOperationException("SQL Server DDL Host policy is incomplete.");
    }

    private string PolicyMaterial() => Digest(JsonSerializer.Serialize(new
    {
        _options.Enabled, _options.AllowCreateTable, _options.PolicyRevision,
        schemas = _options.AllowedSchemas.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
        _options.ApprovalLifetime, _options.ExecutionTimeout
    }));

    private CompiledDdlPlan Compile(string sql, SqlProviderCapabilityProfile? profile) =>
        SqlCoreFacade.CompileDdl(sql, profile is null
            ? new DdlCompilationOptions(SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer)
            : new DdlCompilationOptions(SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                profile, profile));

    private static bool IsSystemDatabase(string database) =>
        database.Equals("master", StringComparison.OrdinalIgnoreCase)
        || database.Equals("model", StringComparison.OrdinalIgnoreCase)
        || database.Equals("msdb", StringComparison.OrdinalIgnoreCase)
        || database.Equals("tempdb", StringComparison.OrdinalIgnoreCase);

    private static async Task<object?> ScalarAsync(DbConnection conn, DbTransaction? tx,
        string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(conn, tx, sql, parameters);
        return await command.ExecuteScalarAsync(token);
    }

    private static async Task ExecuteAsync(DbConnection conn, DbTransaction tx,
        string sql, CancellationToken token)
    {
        await using var command = Command(conn, tx, sql);
        await command.ExecuteNonQueryAsync(token);
    }

    private static DbCommand Command(DbConnection conn, DbTransaction? tx,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.CommandTimeout = 15;
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            command.Parameters.Add(p);
        }
        return command;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static SqlServerDdlRuntimeResult Deny(
        string code, string message, SqlServerDdlRuntimePreview? preview = null) =>
        new(DdlRuntimeStatus.Rejected, preview, message, code);

    private static SqlServerDdlRuntimeResult Unknown(
        SqlServerDdlRuntimePreview preview, string message) =>
        new(DdlRuntimeStatus.OutcomeUnknown, preview,
            message + " Do not retry automatically; reconcile physical SQL Server metadata.",
            "ddl.outcome_unknown");
}
