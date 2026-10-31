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

namespace HsSqlAgent.Server.Services;

/// <summary>
/// A MySQL 8.4 LTS InnoDB implicit-commit CREATE TABLE host. No explicit DB
/// transactions are opened: successful atomic DDL cannot be user-rolled-back.
/// GET_LOCK coordinates this Host only; it cannot lock out external DDL clients.
/// </summary>
public sealed class MySqlDdlRuntime(
    MySqlDdlExecutionOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly MySqlDdlExecutionOptions _options = options ?? new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public bool Enabled => _options.Enabled;

    public async Task<MySqlDdlRuntimeResult> RunAsync(
        ISqlProvider provider, string connectionString, string sql,
        DdlActorContext actor, bool previewOnly,
        IDdlApprovalProvider? approvalProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_options.Enabled)
            return Deny("ddl.disabled", "MySQL DDL is disabled by default.");
        if (provider.Type != SqlAgentToolType.MySQL)
            return Deny("ddl.provider_denied", "MySQL-only DDL adapter.");
        if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(connectionString)
            || actor is null || string.IsNullOrWhiteSpace(actor.DatabaseName)
            || string.IsNullOrWhiteSpace(actor.PrincipalIdentity)
            || string.IsNullOrWhiteSpace(actor.TargetIdentity))
            return Deny("ddl.context_missing", "Authenticated actor and target are required.");
        if (!previewOnly && approvalProvider is null)
            return Deny("ddl.approval_required", "Separate DDL approval is required.");

        CompiledDdlPlan plan;
        string table;
        try
        {
            ValidateOptions();
            plan = Compile(sql, null);
            var change = plan.Changes[0];
            if (change.ObjectIdentity is null || change.ObjectIdentity.Parts.Count != 1)
                return Deny("ddl.unqualified_identity_required",
                    "MySQL G5 requires one table in the selected database.");
            table = change.ObjectIdentity.Parts[0];
            if (!_options.AllowCreateTable || change.Operation != DdlSchemaOperation.CreateTable)
                return Deny("ddl.operation_denied", "Only CREATE TABLE is proven for MySQL.");
            if (change.ColumnDefinitions.Count is < 1 or > 16
                || change.ColumnDefinitions.Any(c =>
                    !string.Equals(c.TypeName, "INT", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(c.TypeName, "INTEGER", StringComparison.OrdinalIgnoreCase)))
                return Deny("ddl.type_denied", "Only 1–16 simple signed INT columns are proven.");
            if (plan.Command.Parameters.Count != 0
                || plan.TransactionBehavior != DdlTransactionBehavior.ImplicitCommit)
                return Deny("ddl.transaction_denied", "MySQL requires implicit-commit DDL semantics.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Deny("ddl.compile_denied", "SQL is outside the proven MySQL DDL grammar.");
        }

        try
        {
            MySqlDdlPhysicalSnapshot before;
            await using (var connection = provider.Connections.Create(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                before = await SnapshotAsync(connection, table, cancellationToken);
                if (before.DatabaseName != actor.DatabaseName
                    || !_options.AllowedDatabases.Contains(before.DatabaseName))
                    return Deny("ddl.database_denied",
                        "The observed physical MySQL database is not explicitly allowed.");
                if (before.TableExists)
                    return Deny("ddl.physical_precondition", "MySQL table name already exists.");
            }

            var version = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL,
                new Version(8, 4));
            plan = Compile(sql, version);
            var staticPreview = SqlCoreFacade.PreviewDdl(sql,
                new DdlCompilationOptions(SqlAgentToolType.MySQL,
                    SqlAgentToolType.MySQL, version, version));
            var expected = plan.Changes[0].ColumnDefinitions
                .Select(c => new DdlPhysicalColumn(c.Name, "int", c.Nullable))
                .ToArray();
            var policy = PolicyMaterial();
            var now = _time.GetUtcNow();
            var expires = now + _options.ApprovalLifetime;
            var requestId = "ddl_" + Guid.NewGuid().ToString("N");
            var summary = "MySQL 8.4 InnoDB CREATE TABLE " + before.DatabaseName + "." + table
                + "\nBefore: absent\nExpected columns: "
                + string.Join(", ", expected.Select(c => c.Name + " INT "
                    + (c.Nullable ? "NULL" : "NOT NULL")))
                + "\nRisk: implicit commit; never user-rollbackable. "
                + "Estimated lock duration and affected rows: unknown.";
            var fingerprint = Digest(JsonSerializer.Serialize(new
            {
                domain = "mysql84-ddl-v1", plan.PlanFingerprint,
                nativeSql = plan.Command.Sql, before.ServerIdentity,
                before.Fingerprint, actor.PrincipalIdentity, actor.TargetIdentity,
                actor.DatabaseName, policy, requestId,
                issued = now.ToUnixTimeMilliseconds(),
                expires = expires.ToUnixTimeMilliseconds(), summary
            }));
            var preview = new MySqlDdlRuntimePreview(staticPreview, before,
                Array.AsReadOnly(expected), fingerprint, expires, requestId, summary);
            if (previewOnly)
                return new(DdlRuntimeStatus.Preview, preview,
                    "Read-only MySQL physical metadata preview; no DDL executed.");

            DdlApprovalDecision decision;
            try
            {
                decision = await approvalProvider!.RequestAsync(new DdlApprovalRequest(
                    requestId, fingerprint, plan.PlanFingerprint,
                    actor.PrincipalIdentity, actor.TargetIdentity, before.ServerIdentity,
                    _options.PolicyRevision, now, expires, summary), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Deny("ddl.approval_cancelled", "MySQL DDL approval cancelled.", preview);
            }
            catch (Exception)
            {
                return Deny("ddl.approval_provider_failed",
                    "Independent MySQL DDL approval failed.", preview);
            }
            if (decision is null
                || !string.Equals(decision.RequestId, requestId, StringComparison.Ordinal)
                || !string.Equals(decision.ApprovalFingerprint, fingerprint, StringComparison.Ordinal))
                return Deny("ddl.approval_mismatch", "DDL approval fingerprint mismatch.", preview);
            if (decision.State == DdlApprovalState.Pending)
                return new(DdlRuntimeStatus.Pending, preview,
                    "Pending DDL approval cannot be resumed; request a new preview.");
            if (decision.State != DdlApprovalState.Approved
                || string.IsNullOrWhiteSpace(decision.ApproverIdentity)
                || string.Equals(decision.ApproverIdentity, actor.PrincipalIdentity,
                    StringComparison.Ordinal))
                return Deny("ddl.approval_denied", "Independent DDL approval denied.", preview);
            if (_time.GetUtcNow() >= expires || policy != PolicyMaterial())
                return Deny("ddl.policy_changed", "MySQL approval expired or policy changed.", preview);

            return await ExecuteOnceAsync(provider, connectionString, plan, preview,
                policy, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Deny("ddl.cancelled_before_execution",
                "MySQL DDL cancelled before native execution.");
        }
        catch (Exception)
        {
            return Deny("ddl.preview_failed", "MySQL physical metadata is unproven.");
        }
    }

    private async Task<MySqlDdlRuntimeResult> ExecuteOnceAsync(
        ISqlProvider provider, string connectionString, CompiledDdlPlan plan,
        MySqlDdlRuntimePreview preview, string approvedPolicy,
        CancellationToken cancellationToken)
    {
        // There is deliberately NO BeginTransaction. MySQL CREATE TABLE
        // commits implicitly and client rollback cannot undo this statement.
        bool beganNative = false;
        DbConnection? connection = null;
        var lockName = "hsql_g5_" + Digest(preview.Before.ServerIdentity + "|"
            + preview.Before.DatabaseName + "|" + preview.Before.TableName)[..48];
        try
        {
            connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(cancellationToken);
            var acquired = await ScalarAsync(connection, "SELECT GET_LOCK(@name, 3)",
                cancellationToken, ("@name", lockName));
            if (acquired is null || acquired is DBNull
                || Convert.ToInt32(acquired, CultureInfo.InvariantCulture) != 1)
                return Deny("ddl.lock_denied",
                    "MySQL cooperative Host lock was not acquired.", preview);
            var fresh = await SnapshotAsync(connection, preview.Before.TableName,
                cancellationToken);
            if (fresh.Fingerprint != preview.Before.Fingerprint
                || fresh.ServerIdentity != preview.Before.ServerIdentity
                || fresh.TableExists || _time.GetUtcNow() >= preview.ExpiresAt
                || approvedPolicy != PolicyMaterial())
                return Deny("ddl.schema_drift",
                    "MySQL physical metadata, identity, approval or policy changed.", preview);

            beganNative = true;
            await using (var command = Command(connection, plan.Command.Sql))
                await command.ExecuteNonQueryAsync(cancellationToken);
            // The server has already committed the CREATE. Never attempt
            // ROLLBACK on failed postcondition verification.
            var after = await SnapshotAsync(connection, preview.Before.TableName,
                cancellationToken);
            if (!after.TableExists
                || after.StorageEngine != "InnoDB"
                || after.ServerIdentity != preview.Before.ServerIdentity
                || after.DatabaseName != preview.Before.DatabaseName
                || after.Columns.Count != preview.ExpectedColumns.Count
                || !after.Columns.Zip(preview.ExpectedColumns).All(pair =>
                    string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                    && string.Equals(pair.First.TypeName, pair.Second.TypeName,
                        StringComparison.OrdinalIgnoreCase)
                    && pair.First.Nullable == pair.Second.Nullable))
                return Unknown(preview,
                    "MySQL native DDL returned but physical postcondition did not match.");

            return new(DdlRuntimeStatus.Committed, preview,
                "MySQL InnoDB atomic CREATE TABLE completed and physical metadata verified. "
                + "It was an implicit-commit statement, not a user transaction.");
        }
        catch (Exception)
        {
            // Even if ExecuteNonQuery threw, a lost server reply can mean the
            // statement committed. Never label this proven rollback/failed.
            if (beganNative)
                return Unknown(preview, "MySQL DDL may have committed despite lost reply.");
            return Deny("ddl.pre_execution_failed",
                "MySQL connection or precondition failed before native DDL.", preview);
        }
        finally
        {
            // Session locks survive implicit commits and release with connection
            // close. Best-effort explicit release cannot change DDL outcome.
            if (connection is not null)
            {
                try
                {
                    if (connection.State == ConnectionState.Open)
                        await ScalarAsync(connection, "SELECT RELEASE_LOCK(@name)",
                            CancellationToken.None, ("@name", lockName));
                }
                catch (Exception) { }
                try { await connection.DisposeAsync(); } catch (Exception) { }
            }
        }
    }

    private async Task<MySqlDdlPhysicalSnapshot> SnapshotAsync(
        DbConnection connection, string table, CancellationToken token)
    {
        string uuid, database, version, versionComment, engine, user;
        int caseMode;
        await using (var command = Command(connection,
            "SELECT @@server_uuid, DATABASE(), VERSION(), @@version_comment, "
            + "@@session.default_storage_engine, @@global.lower_case_table_names, CURRENT_USER()"))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token) || Enumerable.Range(0, 7).Any(reader.IsDBNull))
                throw new InvalidOperationException("MySQL server identity is unproven.");
            uuid = reader.GetValue(0).ToString()!.Trim();
            database = reader.GetString(1);
            version = reader.GetString(2);
            versionComment = reader.GetString(3);
            engine = reader.GetString(4);
            caseMode = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture);
            user = reader.GetString(6);
        }
        if (!Guid.TryParse(uuid, out _) || !IsProvenMySql84(version, versionComment)
            || !string.Equals(engine, "InnoDB", StringComparison.OrdinalIgnoreCase)
            || caseMode != 0
            || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(user)
            || IsSystemDatabase(database))
            throw new InvalidOperationException(
                "MySQL 8.4, InnoDB default engine, exact-case metadata and real UUID required.");

        bool exists = false;
        string? targetEngine = null;
        await using (var command = Command(connection,
            "SELECT TABLE_TYPE, ENGINE FROM information_schema.TABLES "
            + "WHERE BINARY TABLE_SCHEMA=BINARY @db AND BINARY TABLE_NAME=BINARY @table",
            ("@db", database), ("@table", table)))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (await reader.ReadAsync(token))
            {
                if (reader.IsDBNull(0) || !string.Equals(reader.GetString(0),
                    "BASE TABLE", StringComparison.OrdinalIgnoreCase)
                    || reader.IsDBNull(1))
                    throw new InvalidOperationException("Unmodeled MySQL object or VIEW.");
                exists = true;
                targetEngine = reader.GetString(1);
                if (!string.Equals(targetEngine, "InnoDB", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only InnoDB atomic DDL is proven.");
            }
        }

        var columns = new List<DdlPhysicalColumn>();
        if (exists)
        {
            await using var command = Command(connection,
                "SELECT COLUMN_NAME, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE, "
                + "EXTRA, GENERATION_EXPRESSION, COLUMN_DEFAULT, COLUMN_KEY "
                + "FROM information_schema.COLUMNS "
                + "WHERE BINARY TABLE_SCHEMA=BINARY @db AND BINARY TABLE_NAME=BINARY @table "
                + "ORDER BY ORDINAL_POSITION",
                ("@db", database), ("@table", table));
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)
                    || reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5)
                    || reader.IsDBNull(7) || columns.Count == 16
                    || !string.Equals(reader.GetString(1), "int", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(reader.GetString(2), "int", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrEmpty(reader.GetString(4))
                    || !string.IsNullOrEmpty(reader.GetString(5))
                    || !reader.IsDBNull(6)
                    || !string.IsNullOrEmpty(reader.GetString(7)))
                    throw new InvalidOperationException("Unmodeled MySQL column/default/index metadata.");
                columns.Add(new DdlPhysicalColumn(reader.GetString(0), "int",
                    reader.GetString(3) == "YES"));
            }
        }

        var identity = Digest(JsonSerializer.Serialize(new
        {
            uuid, version, versionComment, database, engine, caseMode, user
        }));
        var fingerprint = Digest(JsonSerializer.Serialize(new
        {
            identity, database, table, exists, targetEngine, columns
        }));
        return new(identity, database, table, Digest(user), exists,
            targetEngine, columns.AsReadOnly(), fingerprint);
    }

    internal static bool IsProvenMySql84(string? version, string? comment) =>
        !string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(comment)
        && comment.Contains("MySQL", StringComparison.OrdinalIgnoreCase)
        && !comment.Contains("MariaDB", StringComparison.OrdinalIgnoreCase)
        && !comment.Contains("Percona", StringComparison.OrdinalIgnoreCase)
        && Version.TryParse(version.Split('-', 2)[0], out var parsed)
        && parsed.Major == 8 && parsed.Minor == 4;

    private static bool IsSystemDatabase(string database) =>
        database.Equals("mysql", StringComparison.OrdinalIgnoreCase)
        || database.Equals("sys", StringComparison.OrdinalIgnoreCase)
        || database.Equals("information_schema", StringComparison.OrdinalIgnoreCase)
        || database.Equals("performance_schema", StringComparison.OrdinalIgnoreCase);

    private void ValidateOptions()
    {
        if (!_options.AllowCreateTable || _options.AllowedDatabases.Count == 0
            || string.IsNullOrWhiteSpace(_options.PolicyRevision)
            || _options.ApprovalLifetime <= TimeSpan.Zero
            || _options.ApprovalLifetime > TimeSpan.FromMinutes(5)
            || _options.ExecutionTimeout <= TimeSpan.Zero
            || _options.ExecutionTimeout > TimeSpan.FromSeconds(15))
            throw new InvalidOperationException("MySQL DDL policy is incomplete.");
    }

    private string PolicyMaterial() => Digest(JsonSerializer.Serialize(new
    {
        _options.Enabled, _options.AllowCreateTable, _options.PolicyRevision,
        databases = _options.AllowedDatabases.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        _options.ApprovalLifetime, _options.ExecutionTimeout
    }));

    private static CompiledDdlPlan Compile(string sql, SqlProviderCapabilityProfile? version) =>
        SqlCoreFacade.CompileDdl(sql, version is null
            ? new DdlCompilationOptions(SqlAgentToolType.MySQL, SqlAgentToolType.MySQL)
            : new DdlCompilationOptions(SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
                version, version));

    private async Task<object?> ScalarAsync(DbConnection connection, string sql,
        CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteScalarAsync(token);
    }

    private DbCommand Command(DbConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
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

    private static MySqlDdlRuntimeResult Deny(string code, string message,
        MySqlDdlRuntimePreview? preview = null) =>
        new(DdlRuntimeStatus.Rejected, preview, message, code);

    private static MySqlDdlRuntimeResult Unknown(MySqlDdlRuntimePreview preview, string message) =>
        new(DdlRuntimeStatus.OutcomeUnknown, preview,
            message + " Do not automatically retry. Reconcile physical MySQL metadata.",
            "ddl.outcome_unknown");
}
