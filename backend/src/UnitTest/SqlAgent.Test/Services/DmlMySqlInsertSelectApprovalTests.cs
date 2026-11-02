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

    [Theory]
    [InlineData("between")]
    [InlineData("and")]
    [InlineData("or")]
    public async Task BoundedBetween_MySqlInnoDbNativeSource_ApprovesTwoRowsAndCommits(string kind)
    {
        await WithTablesAsync(async (cs, schema, target, source, version) =>
        {
            await ExecuteAsync(cs,
                $"INSERT INTO `{schema}`.`{source}` (id,name) VALUES (2,'Dave')");
            var predicate = kind switch
            {
                "between" => "id BETWEEN 1 AND 2",
                "and" => "id BETWEEN 1 AND 2 AND name IN ('Carol','Dave')",
                _ => "id BETWEEN 1 AND 1 OR id = 2"
            };
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) " +
                $"SELECT name FROM `{schema}`.`{source}` WHERE {predicate}");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(cs, plan, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.Equal(2, preview.AffectedRows);
            Assert.Equal(new[] { "Carol", "Dave" },
                preview.Rows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            var commit = await coordinator.CommitAsync(cs, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(commit.Committed);
            Assert.Equal(2, commit.AffectedRows);
            Assert.Empty(commit.ReturnedRows);
            Assert.Equal(4, await ScalarAsync(cs, $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData("in")]
    [InlineData("or")]
    [InlineData("mixed")]
    public async Task BoundedInOr_RealMySqlInnoDbSourceRows_ApproveExactlyTwoAndCommit(string kind)
    {
        await WithTablesAsync(async (connectionString, schema, target, source, version) =>
        {
            await ExecuteAsync(connectionString,
                $"INSERT INTO `{schema}`.`{source}` (id,name) VALUES (2,'Dave')");
            var predicate = kind switch
            {
                "in" => "id IN (1,2)",
                "or" => "id = 1 OR id = 2",
                _ => "(id = 1 OR id = 2) AND name IN ('Carol','Dave')"
            };
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name " +
                $"FROM `{schema}`.`{source}` WHERE {predicate}");
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(connectionString, plan, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.Equal(2, preview.AffectedRows);
            Assert.Equal(new[] { "Carol", "Dave" },
                preview.Rows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            var result = await coordinator.CommitAsync(connectionString, plan,
                preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(result.Committed);
            Assert.Equal(2, result.AffectedRows);
            Assert.Empty(result.ReturnedRows);
            Assert.Equal(4, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
        });
    }

    [Fact]
    public async Task NativeInnoDb_TwoDirectEqualityTerms_ApprovesExactSourceAndCommitsOnce()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, version) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` " +
                "WHERE id = 1 AND name = 'Carol'";
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, source, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Carol", Assert.Single(preview.Rows)["name"]);
            var result = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(result.Committed);
            Assert.Equal(1, result.AffectedRows);
            Assert.Equal(3, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
        });
    }

    [Fact]
    public void NativeInnoDb_FiveEqualityTerms_RejectsAtHostShapeGate()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO app.target (name) SELECT name FROM app.source " +
            "WHERE id = 1 AND name = 'Carol' AND id = 1 AND name = 'Carol' AND id = 1",
            SqlAgentToolType.MySQL);
        var insert = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.InsertStatement>(parsed.Statement);
        var rejection = Assert.Throws<NotSupportedException>(
            () => NativeInsertSelectSafety.RequireShape(
                insert, SqlAgentToolType.MySQL, SqlAgentToolType.MySQL, maxAffectedRows: 2));
        Assert.Contains("four", rejection.Message, StringComparison.OrdinalIgnoreCase);
    }

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
    public async Task SamePhysicalInnoDbTable_ApprovedSourceRows_CommitOneNativeInsert()
    {
        await WithTablesAsync(async (connectionString, schema, target, _, version) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{target}` WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, target, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Single(plan.SchemaSnapshot!.Tables);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Alice", Assert.Single(preview.Rows)["name"]);
            Assert.Equal(2, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
            var committed = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Empty(committed.ReturnedRows);
            Assert.Equal(3, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task SamePhysicalInnoDbTable_SourceRowDrift_DeniesInsertion()
    {
        await WithTablesAsync(async (connectionString, schema, target, _, version) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{target}` WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, target, sql);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(connectionString,
                $"UPDATE `{schema}`.`{target}` SET name = 'Mallory' WHERE id = 1");
            var rejected = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.False(rejected.Committed);
            Assert.Empty(rejected.ReturnedRows);
            Assert.Equal(2, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
        });
    }

    [Fact]
    public async Task SamePhysicalInnoDbTable_IdenticalDdlReplacement_DeniesOldApproval()
    {
        await WithTablesAsync(async (connectionString, schema, target, _, version) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{target}` WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, target, sql);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(connectionString, $"DROP TABLE `{schema}`.`{target}`");
            await ExecuteAsync(connectionString,
                $"CREATE TABLE `{schema}`.`{target}` (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL) ENGINE=InnoDB");
            await ExecuteAsync(connectionString,
                $"INSERT INTO `{schema}`.`{target}` (name) VALUES ('Alice'), ('Bob')");
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
        });
    }

    [Fact]
    public async Task SamePhysicalInnoDbTable_SerializableSchemaProof_HoldsSourceWriteLock()
    {
        await WithTablesAsync(async (connectionString, schema, target, _, _) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{target}` WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, target, sql);
            await using var first = new MySqlConnection(connectionString);
            await first.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await first.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, TestContext.Current.CancellationToken);
            Assert.Equal(plan.SchemaSnapshot!.Sha256, await native.ReadCurrentSchemaDigestAsync(
                first, transaction, SqlAgentToolType.MySQL, plan.SchemaSnapshot,
                TestContext.Current.CancellationToken));
            await using var second = new MySqlConnection(connectionString);
            await second.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = second.CreateCommand();
            cmd.CommandText = "SET SESSION innodb_lock_wait_timeout = 1";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            cmd.CommandText = $"UPDATE `{schema}`.`{target}` SET name = 'Hijacked' WHERE id = 1";
            var error = await Assert.ThrowsAsync<MySqlException>(() =>
                cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1205, error.Number);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task SamePhysicalInnoDbTable_WeakerTransactionIsolation_DeniesFreshnessProof()
    {
        await WithTablesAsync(async (connectionString, schema, target, _, _) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{target}` WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, target, sql);
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, TestContext.Current.CancellationToken);
            Assert.Null(await native.ReadCurrentSchemaDigestAsync(
                connection, transaction, SqlAgentToolType.MySQL, plan.SchemaSnapshot!,
                TestContext.Current.CancellationToken));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task SamePhysicalInnoDbTable_IndexDrift_InvalidatesApproval()
    {
        await WithTablesAsync(async (connectionString, schema, target, _, version) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{target}` WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, target, sql);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(connectionString,
                $"CREATE INDEX `ix_m09_self_{target}` ON `{schema}`.`{target}` (name)");
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
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
    public async Task SameDdlSameSourceValues_ButRecreatedPhysicalTable_RejectsOldApproval()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, version) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, source, sql);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(connectionString, $"DROP TABLE `{schema}`.`{source}`");
            await ExecuteAsync(connectionString,
                $"CREATE TABLE `{schema}`.`{source}` (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL) ENGINE=InnoDB");
            await ExecuteAsync(connectionString,
                $"INSERT INTO `{schema}`.`{source}` (id, name) VALUES (1, 'Carol')");
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
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
    [InlineData("SELECT name FROM {0} WHERE id = 1 OR 1 = 1")]
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

    [Fact]
    public async Task ExcessSourceRows_AreDeniedBeforeInsert()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, version) =>
        {
            await ExecuteAsync(connectionString,
                $"INSERT INTO `{schema}`.`{source}` (id,name) VALUES (2,'Carol'),(3,'Carol')");
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE name = 'Carol'");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM `{schema}`.`{target}`"));
        });
    }

    [Fact]
    public async Task UserTriggerOnSource_FailsClosedEvenWhenTriggerIsNotPartOfTableDdl()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, _) =>
        {
            await ExecuteAsync(connectionString,
                $"CREATE TRIGGER `tr_m09_{source}` BEFORE INSERT ON `{schema}`.`{source}` " +
                "FOR EACH ROW SET NEW.name = NEW.name");
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() => CreatePlanAsync(
                provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE id = 1"));
        });
    }

    [Fact]
    public async Task SourceForeignKey_FailsClosedBeforeApproval()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, _) =>
        {
            // The target already has unique primary-key rows 1 and 2.
            await ExecuteAsync(connectionString,
                $"ALTER TABLE `{schema}`.`{source}` ADD CONSTRAINT `fk_m09_{source}` " +
                $"FOREIGN KEY (id) REFERENCES `{schema}`.`{target}` (id)");
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() => CreatePlanAsync(
                provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE id = 1"));
        });
    }

    [Fact]
    public async Task MissingIndependentSourceGrant_FailsClosed()
    {
        await WithTablesAsync(async (connectionString, schema, target, source, _) =>
        {
            var provider = new MySqlProvider();
            var native = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreatePlanAsync(
                provider, native, connectionString, schema, target, source,
                $"INSERT INTO `{schema}`.`{target}` (name) SELECT name FROM `{schema}`.`{source}` WHERE id = 1",
                grantSource: false));
        });
    }

    private static async Task<ValidatedDmlPlan> CreatePlanAsync(
        MySqlProvider provider, NativeDmlSchemaSnapshotService native,
        string connString, string schema, string target, string source, string sql,
        bool grantSource = true)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MySQL);
        await using var conn = provider.Connections.Create(connString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var snapshot = await native.CaptureAsync(
            conn, SqlAgentToolType.MySQL, parsed, TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { schema + "." + target };
        if (grantSource) allowed.Add(schema + "." + source);
        var context = new SqlPlanValidationContext("mysql84-m09-v1", allowed)
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
        await using var schemaCommand = new MySqlCommand("SELECT DATABASE()", c);
        var schema = (string)(await schemaCommand.ExecuteScalarAsync(
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("MySQL current database is not set."));
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
