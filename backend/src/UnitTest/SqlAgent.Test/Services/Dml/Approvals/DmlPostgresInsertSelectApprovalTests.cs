using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Actual PostgreSQL SQL + native schema + strict approval; not a mocked compiler.</summary>
public sealed class DmlPostgresInsertSelectApprovalTests
{
    private const string Approval = "postgres-insert-select-source-row-approval-v1";


    [Theory]
    [InlineData("null", "Dave", 1)]
    [InlineData("not-null", "Carol", 1)]
    [InlineData("mixed", "Carol,Dave", 2)]
    public async Task NativeNullableColumnPredicate_PostgresRealApprovalAndReturnedRows(
        string kind, string names, int affected)
    {
        await WithTablesAsync(async (cs, schema, version) =>
        {
            await ExecuteAsync(cs, $"INSERT INTO {schema}.source_users(id,name) VALUES (2,'Dave')");
            await ExecuteAsync(cs, $"ALTER TABLE {schema}.source_users ADD COLUMN note TEXT NULL");
            await ExecuteAsync(cs, $"UPDATE {schema}.source_users SET note = 'marked' WHERE id = 1");
            var predicate = kind switch
            {
                "null" => "note IS NULL",
                "not-null" => "note IS NOT NULL",
                _ => "(note IS NULL OR id = 1) AND name IS NOT NULL"
            };
            var provider = new PostgresProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users " +
                $"WHERE {predicate} RETURNING id, name");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(cs, plan, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.Equal(affected, preview.AffectedRows);
            Assert.Equal(names.Split(','),
                preview.Rows.Select(row => (string)row["name"]!).OrderBy(name => name).ToArray());
            var committed = await coordinator.CommitAsync(cs, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(affected, committed.AffectedRows);
            Assert.Equal(names.Split(','),
                committed.ReturnedRows.Select(row => (string)row["name"]!).OrderBy(name => name).ToArray());
            Assert.All(committed.ReturnedRows, row => Assert.True(Convert.ToInt32(row["id"]) > 2));
            Assert.Equal(2 + affected, await ScalarAsync(cs, $"SELECT COUNT(*) FROM {schema}.users"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData("between")]
    [InlineData("and")]
    [InlineData("or")]
    public async Task BoundedBetween_PostgresNativeSource_ReturnsTwoActualInsertedRows(string kind)
    {
        await WithTablesAsync(async (cs, schema, version) =>
        {
            await ExecuteAsync(cs, $"INSERT INTO {schema}.source_users(id,name) VALUES (2,'Dave')");
            var predicate = kind switch
            {
                "between" => "id BETWEEN 1 AND 2",
                "and" => "id BETWEEN 1 AND 2 AND name IN ('Carol','Dave')",
                _ => "id BETWEEN 1 AND 1 OR id = 2"
            };
            var provider = new PostgresProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users " +
                $"WHERE {predicate} RETURNING id, name");
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
            Assert.Equal(new[] { "Carol", "Dave" },
                commit.ReturnedRows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { 3, 4 },
                commit.ReturnedRows.Select(x => Convert.ToInt32(x["id"])).OrderBy(x => x).ToArray());
            Assert.Equal(4, await ScalarAsync(cs, $"SELECT COUNT(*) FROM {schema}.users"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData("in")]
    [InlineData("or")]
    [InlineData("mixed")]
    public async Task BoundedInOr_TwoActualPostgresSourceRows_ApprovedAndReturnNativeRows(string kind)
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            await ExecuteAsync(connectionString,
                $"INSERT INTO {schema}.source_users(id,name) VALUES (2,'Dave')");
            var predicate = kind switch
            {
                "in" => "id IN (1,2)",
                "or" => "id = 1 OR id = 2",
                _ => "(id = 1 OR id = 2) AND name IN ('Carol','Dave')"
            };
            var provider = new PostgresProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, connectionString, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users " +
                $"WHERE {predicate} RETURNING id, name");
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(connectionString, plan, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.Equal(2, preview.AffectedRows);
            Assert.Equal(new[] { "Carol", "Dave" },
                preview.Rows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            Assert.All(preview.Rows, row => Assert.False(row.ContainsKey("id")));
            var result = await coordinator.CommitAsync(connectionString, plan,
                preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(result.Committed);
            Assert.Equal(2, result.AffectedRows);
            Assert.Equal(new[] { "Carol", "Dave" },
                result.ReturnedRows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { 3, 4 },
                result.ReturnedRows.Select(x => Convert.ToInt32(x["id"])).OrderBy(x => x).ToArray());
            Assert.Equal(4, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Fact]
    public async Task DistinctSource_TwoDirectEqualityTerms_ApprovesRowAndReturnsNativeInsertResult()
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            var provider = new PostgresProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users " +
                "WHERE id = 1 AND name = 'Carol' RETURNING id, name";
            var plan = await CreatePlanAsync(provider, native, connectionString, schema, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
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
            Assert.Equal("Carol", Assert.Single(committed.ReturnedRows)["name"]);
            Assert.Equal(3, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Fact]
    public void DistinctSource_FiveEqualityTerms_RejectsAtHostShapeGate()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO public.users (name) SELECT name FROM public.source_users " +
            "WHERE id = 1 AND name = 'Carol' AND id = 1 AND name = 'Carol' AND id = 1",
            SqlAgentToolType.Postgres);
        var insert = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.InsertStatement>(parsed.Statement);
        var rejection = Assert.Throws<NotSupportedException>(
            () => NativeInsertSelectSafety.RequireShape(
                insert, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, maxAffectedRows: 2));
        Assert.Contains("four", rejection.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DifferentPhysicalTables_PreviewApprovesSourceAndCommitReturnsActualNewRow()
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users WHERE id = 1 RETURNING id, name";
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal(2, plan.SchemaSnapshot!.Tables.Count);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Carol", preview.Rows.Single()["name"]);
            Assert.DoesNotContain(preview.Rows.Single().Keys, column => column.Equals("id", StringComparison.OrdinalIgnoreCase));
            var committed = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            var row = Assert.Single(committed.ReturnedRows);
            Assert.Equal(3, Convert.ToInt32(row["id"]));
            Assert.Equal("Carol", row["name"]);
            Assert.Equal(3, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceValueOrNativeIndexDrift_AfterApprovalDeniesCommit(bool changeIndex)
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            if (changeIndex)
            {
                await ExecuteAsync(connectionString,
                    $"CREATE INDEX idx_m09_source_name ON {schema}.source_users (name)");
                await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, Approval,
                    TestContext.Current.CancellationToken, version));
            }
            else
            {
                await ExecuteAsync(connectionString,
                    $"UPDATE {schema}.source_users SET name = 'Mallory' WHERE id = 1");
                var rejected = await coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, Approval,
                    TestContext.Current.CancellationToken, version);
                Assert.False(rejected.Committed);
                Assert.Empty(rejected.ReturnedRows);
            }
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Fact]
    public async Task ThreeSourceRows_ExceedsPolicyBeforeApproval()
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            await ExecuteAsync(connectionString,
                $"INSERT INTO {schema}.source_users(id,name) VALUES (2,'Carol'),(3,'Carol')");
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users WHERE name = 'Carol'");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Theory]
    [InlineData("SELECT name FROM {0}.source_users")]
    [InlineData("SELECT upper(name) FROM {0}.source_users WHERE id = 1")]
    [InlineData("SELECT s.name FROM {0}.source_users AS s WHERE id = 1")]
    [InlineData("SELECT name FROM {0}.source_users WHERE id = 1 OR 1 = 1")]
    public async Task UnsupportedSourceShape_StaysFailClosed(string sourceTemplate)
    {
        await WithTablesAsync(async (connectionString, schema, _) =>
        {
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() => CreatePlanAsync(
                provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) " + string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, sourceTemplate, schema)));
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Fact]
    public async Task CommitSchemaRevalidation_SourceLockPreventsConcurrentPhysicalWrites()
    {
        await WithTablesAsync(async (connectionString, schema, _) =>
        {
            var provider = new PostgresProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, connectionString, schema,
                $"INSERT INTO {schema}.users(name) SELECT name FROM {schema}.source_users WHERE id = 1");
            await using var first = new NpgsqlConnection(connectionString);
            await first.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await first.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, TestContext.Current.CancellationToken);
            var current = await native.ReadCurrentSchemaDigestAsync(
                first, transaction, SqlAgentToolType.Postgres, plan.SchemaSnapshot!,
                TestContext.Current.CancellationToken);
            Assert.Equal(plan.SchemaSnapshot!.Sha256, current);

            await using var second = new NpgsqlConnection(connectionString);
            await second.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = second.CreateCommand();
            command.CommandText = $"SET lock_timeout = '300ms'; UPDATE {schema}.source_users SET name = 'Mallory' WHERE id = 1";
            var blocked = await Assert.ThrowsAsync<PostgresException>(() =>
                command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Equal("55P03", blocked.SqlState);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, await ScalarAsync(connectionString,
                $"SELECT count(*) FROM {schema}.source_users WHERE name = 'Carol'"));
        });
    }

    [Fact]
    public async Task UserTriggerOnSource_CannotMintExecutablePhysicalApprovalProof()
    {
        await WithTablesAsync(async (connectionString, schema, _) =>
        {
            // Issue physical DDL as separate statements; no dollar-quoted
            // procedure body can be split by the ADO.NET command batcher.
            await ExecuteAsync(connectionString,
                $"CREATE FUNCTION {schema}.m09_unsafe_trigger() RETURNS trigger " +
                "LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END;'");
            await ExecuteAsync(connectionString,
                $"CREATE TRIGGER m09_source BEFORE INSERT ON {schema}.source_users " +
                $"FOR EACH ROW EXECUTE FUNCTION {schema}.m09_unsafe_trigger()");
            var provider = new PostgresProvider();
            var native = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() => CreatePlanAsync(
                provider, native, connectionString, schema,
                $"INSERT INTO {schema}.users(name) SELECT name FROM {schema}.source_users WHERE id = 1"));
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Theory]
    [InlineData("RETURNING *")]
    [InlineData("RETURNING upper(name)")]
    [InlineData("RETURNING id, id")]
    public async Task UnsupportedPostgresReturning_FailsClosed(string returning)
    {
        await WithTablesAsync(async (connectionString, schema, _) =>
        {
            var provider = new PostgresProvider();
            var native = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() => CreatePlanAsync(
                provider, native, connectionString, schema,
                $"INSERT INTO {schema}.users(name) SELECT name FROM {schema}.source_users WHERE id = 1 " + returning));
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Fact]
    public async Task MissingPhysicalSourceGrant_DeniesExecutableInsertPlan()
    {
        await WithTablesAsync(async (connectionString, schema, _) =>
        {
            var provider = new PostgresProvider();
            var native = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreatePlanAsync(
                provider, native, connectionString, schema,
                $"INSERT INTO {schema}.users(name) SELECT name FROM {schema}.source_users WHERE id = 1",
                allowSourceGrant: false));
            Assert.Equal(2, await ScalarAsync(connectionString,
                $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Fact]
    public async Task SamePhysicalTable_PreviewAndCommit_ApprovesSourceAndReturnsGeneratedTargetRow()
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.users WHERE id = 1 RETURNING id, name");
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Single(plan.SchemaSnapshot!.Tables);
            Assert.StartsWith("native-postgres-insert-source-v1:", plan.SchemaSnapshot.Version);

            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Alice", Assert.Single(preview.Rows)["name"]);
            Assert.DoesNotContain(preview.Rows[0].Keys,
                column => string.Equals(column, "id", StringComparison.OrdinalIgnoreCase));

            var committed = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            var inserted = Assert.Single(committed.ReturnedRows);
            Assert.Equal(3, Convert.ToInt32(inserted["id"]));
            Assert.Equal("Alice", inserted["name"]);
            Assert.Equal(3, await ScalarAsync(connectionString, $"SELECT count(*) FROM {schema}.users"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePhysicalTable_ApprovedSourceOrIndexDrift_RejectsWithoutInsert(bool alterSchema)
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.users WHERE id = 1 RETURNING id, name");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);

            if (alterSchema)
            {
                await ExecuteAsync(connectionString,
                    $"CREATE INDEX idx_m09_self_name ON {schema}.users (name)");
                await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, Approval,
                    TestContext.Current.CancellationToken, version));
            }
            else
            {
                await ExecuteAsync(connectionString,
                    $"UPDATE {schema}.users SET name = 'Changed' WHERE id = 1");
                var result = await coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, Approval,
                    TestContext.Current.CancellationToken, version);
                Assert.False(result.Committed);
                Assert.Equal(0, result.AffectedRows);
                Assert.Empty(result.ReturnedRows);
            }

            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT count(*) FROM {schema}.users"));
        });
    }

    [Fact]
    public async Task SamePhysicalTable_SerializableCatalogRecheck_AcquiresWriterLockBeforeFirstRead()
    {
        await WithTablesAsync(async (connectionString, schema, _) =>
        {
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.users WHERE id = 1");

            await using var first = new NpgsqlConnection(connectionString);
            await first.OpenAsync(TestContext.Current.CancellationToken);
            await using var tx = await first.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, TestContext.Current.CancellationToken);
            var current = await snapshot.ReadCurrentSchemaDigestAsync(
                first, tx, SqlAgentToolType.Postgres, plan.SchemaSnapshot!,
                TestContext.Current.CancellationToken);
            Assert.Equal(plan.SchemaSnapshot!.Sha256, current);

            await using var rival = new NpgsqlConnection(connectionString);
            await rival.OpenAsync(TestContext.Current.CancellationToken);
            await using var write = rival.CreateCommand();
            write.CommandText =
                $"SET lock_timeout = '300ms'; UPDATE {schema}.users SET name = 'Hijacked' WHERE id = 1";
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                write.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Equal("55P03", denied.SqlState);
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, await ScalarAsync(connectionString,
                $"SELECT count(*) FROM {schema}.users WHERE name = 'Alice'"));
        });
    }

    [Fact]
    public async Task SamePhysicalTable_ExcessSourceRows_DeniesBeforeMutation()
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            await ExecuteAsync(connectionString,
                $"INSERT INTO {schema}.users(id,name) VALUES(4,'Alice'),(5,'Alice')");
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.users WHERE name = 'Alice'");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(4, await ScalarAsync(connectionString, $"SELECT count(*) FROM {schema}.users"));
        });
    }

    private static async Task<ValidatedDmlPlan> CreatePlanAsync(
        PostgresProvider provider, NativeDmlSchemaSnapshotService snapshot,
        string connectionString, string schema, string sql, bool allowSourceGrant = true)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        await using var connection = provider.Connections.Create(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var proof = await snapshot.CaptureAsync(
            connection, SqlAgentToolType.Postgres, parsed, TestContext.Current.CancellationToken);
        Assert.NotNull(proof);
        var grants = new HashSet<string>(StringComparer.Ordinal) { schema + ".users" };
        if (allowSourceGrant) grants.Add(schema + ".source_users");
        var context = new SqlPlanValidationContext("postgres-m09-v1", grants)
            .WithSchemaSnapshot(proof);
        return await new DmlPlanFactory(provider.Metadata).CreateWithMetadataConnectionAsync(
            connection, connectionString, parsed, SqlAgentToolType.Postgres, context,
            maxAffectedRows: 2, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task WithTablesAsync(Func<string,string,string,Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION required for real M09 PostgreSQL integration.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
        }
        var schema = "hsa_m09_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteAsync(connectionString!, $"CREATE SCHEMA {schema}");
        try
        {
            await ExecuteAsync(connectionString!, $"""
                CREATE TABLE {schema}.users (id INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO {schema}.users(id,name) VALUES (1,'Alice'),(2,'Bob');
                SELECT setval(pg_get_serial_sequence('{schema}.users','id'),2,true);
                CREATE TABLE {schema}.source_users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO {schema}.source_users(id,name) VALUES (1,'Carol');
                """);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await test(connectionString!, schema, connection.ServerVersion.Trim());
        }
        finally
        {
            await ExecuteAsync(connectionString!, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
