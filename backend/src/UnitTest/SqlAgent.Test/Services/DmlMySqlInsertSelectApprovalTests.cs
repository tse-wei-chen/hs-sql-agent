using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using MySql.Data.MySqlClient;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Real MySQL 8.4 InnoDB, never a mocked schema/lock proof.</summary>
public sealed class DmlMySqlInsertSelectApprovalTests
{
    private const string Approval = "mysql84-innodb-m09-source-row-approval-v1";

    [Fact]
    public async Task TwoPhysicalInnoDbTables_ApprovedSourceRows_CommitExactInsertedRow()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, version) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE id = 1");
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal(2, plan.SchemaSnapshot!.Tables.Count);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Carol", Assert.Single(preview.Rows)["name"]);
            var committed = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Empty(committed.ReturnedRows);
            Assert.Equal(3, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task SourceRowChange_AfterPreview_DeniesAndRollsBack()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, version) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(connectionString,
                $"UPDATE `{schema}`.`{source}` SET name = 'Mallory' WHERE id = 1");
            var rejected = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.False(rejected.Committed);
            Assert.Empty(rejected.ReturnedRows);
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
        });
    }

    [Fact]
    public async Task SourceIndexDrift_AfterPreview_DeniesCommit()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, version) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(connectionString,
                $"CREATE INDEX `ix_m09_{source}` ON `{schema}`.`{source}` (name)");
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
        });
    }

    [Fact]
    public async Task SerializableTransaction_SourceLockPreventsConcurrentChange()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, _) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE id = 1");
            await using var first = new MySqlConnection(connectionString);
            await first.OpenAsync(TestContext.Current.CancellationToken);
            await using var tx = await first.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, TestContext.Current.CancellationToken);
            Assert.Equal(plan.SchemaSnapshot!.Sha256, await native.ReadCurrentSchemaDigestAsync(
                first, tx, SqlAgentToolType.MySQL, plan.SchemaSnapshot!,
                TestContext.Current.CancellationToken));
            await using var second = new MySqlConnection(connectionString);
            await second.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = second.CreateCommand();
            cmd.CommandText = "SET SESSION innodb_lock_wait_timeout = 1";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            cmd.CommandText = $"UPDATE `{schema}`.`{source}` SET name = 'Hijacked' WHERE id = 1";
            var error = await Assert.ThrowsAsync<MySqlException>(() =>
                cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1205, error.Number);
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
        });
    }

    [Theory]
    [InlineData("RETURNING name")]
    [InlineData("SELECT upper(name) FROM {0} WHERE id = 1")]
    [InlineData("SELECT name FROM {0} WHERE id = 1 OR id = 2")]
    [InlineData("SELECT name FROM {0}")]
    public async Task UnprovenMutationShape_FailsClosed(string suffix)
    {
        await WithTablesAsync(async (connectionString, schema, target, source, _) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sourceName = $"`{schema}`.`{source}`";
            var sql = suffix.StartsWith("SELECT ", StringComparison.Ordinal)
                ? $"INSERT INTO `{schema}`.`{target}` (name) " + string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, suffix, sourceName)
                : $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM {sourceName} WHERE id = 1 " + suffix;
            await Assert.ThrowsAnyAsync<Exception>(() => CreatePlanAsync(
                provider, native, connectionString, schema, target, source, sql));
        });
    }

    [Fact]
    public async Task NonTransactionalMyIsamTarget_FailsBeforeApproval()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, _) =>
        {
            await ExecuteAsync(connectionString, $"ALTER TABLE `{schema}`.`{target}` ENGINE=MyISAM");
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() => CreatePlanAsync(
                provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE id = 1"));
        });
    }

    private static async Task<ValidatedDmlPlan> CreatePlanAsync(
        MySqlProvider provider, NativeDmlSchemaSnapshotService native,
        string connString, string schema, string target, string source, string sql)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MySQL);
        await using var conn = provider.Connections.Create(connString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var snapshot = await native.CaptureAsync(
            conn, SqlAgentToolType.MySQL, parsed, TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        var context = new SqlPlanValidationContext("mysql84-m09-v1",
            new HashSet<string>(StringComparer.Ordinal) { schema + "." + target, schema + "." + source })
            .WithSchemaSnapshot(snapshot);
        return await new DmlPlanFactory(provider.Metadata).CreateWithMetadataConnectionAsync(
            conn, connString, parsed, SqlAgentToolType.MySQL, context,
            maxAffectedRows: 2, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task WithTablesAsync(
        Func<string, string, string, string, string, Task> action)
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_MYSQL_CONNECTION required for native M09 MySQL 8.4 tests.");
            Assert.Skip("Requires TEST_MYSQL_CONNECTION");
        }
        await using var c = new MySqlConnection(connectionString);
        await c.OpenAsync(TestContext.Current.CancellationToken);
        var schema = Convert.ToString(await ScalarAsync(connectionString!, "SELECT DATABASE()"))!;
        Assert.False(string.IsNullOrWhiteSpace(schema));
        var tag = Guid.NewGuid().ToString("N")[..12];
        var target = "m09_target_" + tag;
        var source = "m09_source_" + tag;
        await ExecuteAsync(connectionString!, $"CREATE TABLE `{schema}`.`{target}` (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL) ENGINE=InnoDB");
        try
        {
            await ExecuteAsync(connectionString!, $"CREATE TABLE `{schema}`.`{source}` (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL) ENGINE=InnoDB");
            try
            {
                await ExecuteAsync(connectionString!, $"INSERT INTO `{schema}`.`{target}` (name) VALUES ('Alice'), ('Bob')");
                await ExecuteAsync(connectionString!, $"INSERT INTO `{schema}`.`{source}` (id, name) VALUES (1, 'Carol')");
                await action(connectionString!, schema, target, source, c.ServerVersion.Trim());
            }
            finally { await ExecuteAsync(connectionString!, $"DROP TABLE IF EXISTS `{schema}`.`{source}`"); }
        }
        finally { await ExecuteAsync(connectionString!, $"DROP TABLE IF EXISTS `{schema}`.`{target}`"); }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new MySqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new MySqlCommand(sql, connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
