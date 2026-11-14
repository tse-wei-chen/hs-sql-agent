using System.Data;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlSqliteInsertSelectApprovalTests
{
    private const string Approval = "sqlite-self-source-approval-v1";
    private const string Policy = "sqlite-self-source-policy-v1";
    private const string InsertSql =
        "INSERT INTO main.users (name) SELECT name FROM main.users WHERE id = 1";


    [Theory]
    [InlineData("null", "Dave", 1)]
    [InlineData("not-null", "Carol", 1)]
    [InlineData("mixed", "Carol,Dave", 2)]
    public async Task NativeNullableColumnPredicate_SqliteApprovalAndActualReturnedRows(
        string kind, string names, int affected)
    {
        await WithDatabaseAsync(async (cs, version) =>
        {
            await ExecuteAsync(cs,
                "INSERT INTO main.other_users(id,name) VALUES (1,'Carol'),(2,'Dave')");
            await ExecuteAsync(cs, "ALTER TABLE main.other_users ADD COLUMN note TEXT NULL");
            await ExecuteAsync(cs, "UPDATE main.other_users SET note = 'marked' WHERE id = 1");
            var predicate = kind switch
            {
                "null" => "note IS NULL",
                "not-null" => "note IS NOT NULL",
                _ => "(note IS NULL OR id = 1) AND name IS NOT NULL"
            };
            var provider = new SqliteProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs,
                "INSERT INTO main.users (name) SELECT name FROM main.other_users " +
                $"WHERE {predicate} RETURNING id, name", useVerifiedProfile: true);
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
            Assert.Equal(2 + affected, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite, "main")]
    [InlineData(SqlAgentToolType.Postgres, "public")]
    [InlineData(SqlAgentToolType.MySQL, "app")]
    [InlineData(SqlAgentToolType.MsSqlServer, "dbo")]
    public void NativeNullPredicate_RejectsNonColumnBooleanTautologiesAndExcessiveTerms(
        SqlAgentToolType provider, string schema)
    {
        var rejected = new[]
        {
            "NULL IS NULL",
            "upper(note) IS NULL",
            "NOT (note IS NULL)",
            "note IS NULL OR 1 = 1",
            "note IS NOT NULL AND 1 = 1",
            "note IS NULL OR note IS NOT NULL OR id = 1 OR id = 2 OR id = 3",
            "note IS NULL OR id IN (1,2,3,4)"
        };
        foreach (var predicate in rejected)
        {
            var sql = $"INSERT INTO {schema}.users (name) SELECT name FROM " +
                $"{schema}.source_users WHERE {predicate}";
            try
            {
                var parsed = CoreSqlTextParser.ParseDml(sql, provider);
                var statement = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.InsertStatement>(parsed.Statement);
                Assert.ThrowsAny<Exception>(() => NativeInsertSelectSafety.RequireShape(
                    statement, provider, provider, maxAffectedRows: 2));
            }
            catch (HsSqlAgent.SqlCore.Core.Compilation.SqlCompilationException)
            {
                // Source dialect rejection is also an acceptable fail-closed outcome.
            }
        }
    }

    [Fact]
    public async Task NativeNullPredicate_SourceRowChangedAfterPreview_RejectsWithoutInsertion()
    {
        await WithDatabaseAsync(async (cs, version) =>
        {
            await ExecuteAsync(cs, "INSERT INTO main.other_users(id,name) VALUES (1,'Carol'),(2,'Dave')");
            await ExecuteAsync(cs, "ALTER TABLE main.other_users ADD COLUMN note TEXT NULL");
            await ExecuteAsync(cs, "UPDATE main.other_users SET note = 'marked' WHERE id = 1");
            var provider = new SqliteProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs,
                "INSERT INTO main.users (name) SELECT name FROM main.other_users " +
                "WHERE note IS NULL RETURNING id, name", useVerifiedProfile: true);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(cs, plan, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.Equal("Dave", Assert.Single(preview.Rows)["name"]);
            await ExecuteAsync(cs, "UPDATE main.other_users SET note = NULL WHERE id = 1");
            var rejected = await coordinator.CommitAsync(cs, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.False(rejected.Committed);
            Assert.Empty(rejected.ReturnedRows);
            Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
        });
    }

    [Fact]
    public async Task NativeNullPredicate_ExcessMatchingRows_RejectsAtPreview()
    {
        await WithDatabaseAsync(async (cs, version) =>
        {
            await ExecuteAsync(cs, "INSERT INTO main.other_users(id,name) VALUES (1,'Carol'),(2,'Dave'),(3,'Eve')");
            await ExecuteAsync(cs, "ALTER TABLE main.other_users ADD COLUMN note TEXT NULL");
            var provider = new SqliteProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs,
                "INSERT INTO main.users (name) SELECT name FROM main.other_users " +
                "WHERE note IS NULL", useVerifiedProfile: true);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
        });
    }

    [Theory]
    [InlineData("between")]
    [InlineData("and")]
    [InlineData("or")]
    public async Task BoundedBetween_SqliteNativeSource_ApprovesAndReturnsActualRows(string kind)
    {
        await WithDatabaseAsync(async (cs, version) =>
        {
            await ExecuteAsync(cs, "INSERT INTO main.other_users(id,name) VALUES (1,'Carol'),(2,'Dave')");
            var predicate = kind switch
            {
                "between" => "id BETWEEN 1 AND 2",
                "and" => "id BETWEEN 1 AND 2 AND name IN ('Carol','Dave')",
                _ => "id BETWEEN 1 AND 1 OR id = 2"
            };
            var provider = new SqliteProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs,
                "INSERT INTO main.users (name) SELECT name FROM main.other_users " +
                $"WHERE {predicate} RETURNING id, name", useVerifiedProfile: true);
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
            Assert.Equal(4, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData("in")]
    [InlineData("or")]
    [InlineData("mixed")]
    public async Task BoundedInOr_RealSqliteSourceRows_ApproveTwoAndReturnNativeRows(string kind)
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            await ExecuteAsync(connectionString,
                "INSERT INTO main.other_users (id,name) VALUES (1,'Carol'),(2,'Dave')");
            var predicate = kind switch
            {
                "in" => "id IN (1,2)",
                "or" => "id = 1 OR id = 2",
                _ => "(id = 1 OR id = 2) AND name IN ('Carol','Dave')"
            };
            var provider = new SqliteProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, connectionString,
                "INSERT INTO main.users (name) SELECT name FROM main.other_users " +
                $"WHERE {predicate} RETURNING id, name", useVerifiedProfile: true);
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
            Assert.Equal(new[] { "Carol", "Dave" },
                result.ReturnedRows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { 3, 4 },
                result.ReturnedRows.Select(x => Convert.ToInt32(x["id"])).OrderBy(x => x).ToArray());
            Assert.Equal(4, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM main.users"));
        });
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite, "main")]
    [InlineData(SqlAgentToolType.Postgres, "public")]
    [InlineData(SqlAgentToolType.MySQL, "app")]
    [InlineData(SqlAgentToolType.MsSqlServer, "dbo")]
    public void BoundedInOr_UnsafeBooleanOrExcessiveLiteralShapes_StayFailClosed(
        SqlAgentToolType dialect, string schema)
    {
        var badPredicates = new[]
        {
            "id = 1 OR 1 = 1",
            "id IN (1, 2, 3, 4, 5)",
            "id NOT BETWEEN 1 AND 2",
            "id BETWEEN 1 AND NULL",
            "id BETWEEN NULL AND 2",
            "upper(id) BETWEEN 1 AND 2",
            "1 BETWEEN 1 AND 2",
            "id BETWEEN id AND 2",
            "id BETWEEN 1 AND 2 AND name IN ('A','B','C')",
            "id BETWEEN 1 AND 2 OR id IN (3,4,5)",
            "id NOT IN (1, 2)",
            "id IN (1, NULL)",
            "id = 1 OR id = 2 OR id = 3 OR id = 4 OR id = 5",
            "(id = 1 OR id = 2) AND id IN (1, 2, 3)"
        };
        foreach (var predicate in badPredicates)
        {
            var sql = $"INSERT INTO {schema}.users (name) SELECT name FROM " +
                $"{schema}.source_users WHERE {predicate}";
            try
            {
                var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
                var insert = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.InsertStatement>(parsed.Statement);
                Assert.ThrowsAny<Exception>(() => NativeInsertSelectSafety.RequireShape(
                    insert, dialect, dialect, maxAffectedRows: 2));
            }
            catch (HsSqlAgent.SqlCore.Core.Compilation.SqlCompilationException)
            {
                // Native F# parser rejection also denies access to executable Host approval.
            }
        }
    }

    [Fact]
    public async Task SelfSource_TwoDirectEqualityTerms_ApprovesExactRowAndCommitsOnce()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            var provider = new SqliteProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = "INSERT INTO main.users (name) SELECT name FROM main.users WHERE id = 1 AND name = 'Alice'";
            var plan = await CreatePlanAsync(provider, native, connectionString, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal("Alice", Assert.Single(preview.Rows)["name"]);
            Assert.Equal(1, preview.AffectedRows);
            var commit = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(commit.Committed);
            Assert.Equal(1, commit.AffectedRows);
            Assert.Equal(3, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
        });
    }

    [Fact]
    public async Task SelfSource_FiveEqualityTerms_FailsAtHostShapeGate()
    {
        var sql = "INSERT INTO main.users (name) SELECT name FROM main.users " +
            "WHERE id = 1 AND name = 'Alice' AND id = 1 AND name = 'Alice' AND id = 1";
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite);
        var insert = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.InsertStatement>(parsed.Statement);
        var rejection = Assert.Throws<NotSupportedException>(
            () => NativeInsertSelectSafety.RequireShape(
                insert, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite, maxAffectedRows: 2));
        Assert.Contains("four", rejection.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite, "main")]
    [InlineData(SqlAgentToolType.Postgres, "app")]
    [InlineData(SqlAgentToolType.MySQL, "app")]
    public void FourDistinctEqualityTerms_StayInsideExistingNativeHostShapeBoundary(
        SqlAgentToolType provider, string schema)
    {
        // Exact upper bound: this proves admission of four distinct direct
        // columns without depending on a permissive virtual catalog.
        var sql = $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users " +
            "WHERE id = 1 AND name = 'Carol' AND tenant_id = 7 AND batch_id = 3";
        var parsed = CoreSqlTextParser.ParseDml(sql, provider);
        var insert = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.InsertStatement>(parsed.Statement);
        var shape = NativeInsertSelectSafety.RequireShape(insert, provider, provider, maxAffectedRows: 2);
        Assert.Equal(schema + ".source_users", shape.PhysicalSource);
        Assert.Equal("name", Assert.Single(shape.OutputColumns));
    }

    [Fact]
    public async Task ApprovedSelfSource_InsertSelect_PreviewsExactRowsAndCommitsOnce()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, InsertSql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal("main.users", plan.TableName);

            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Alice", preview.Rows.Single()["name"]);
            Assert.NotNull(preview.Challenge.RowSetFingerprint);

            var committed = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Equal(3, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
            Assert.Equal(2, await ScalarAsync(
                connectionString, "SELECT COUNT(*) FROM users WHERE name = 'Alice'"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
        });
    }

    private const string CrossTableSql =
        "INSERT INTO main.users (name) SELECT name FROM main.other_users WHERE id = 1";

    [Fact]
    public async Task CrossTableSource_PreviewsProjectedValuesAndCommitsExactlyOnce()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            await ExecuteAsync(connectionString,
                "INSERT INTO other_users (id, name) VALUES (1, 'Carol'), (2, 'Diane')");
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, CrossTableSql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal(2, plan.SchemaSnapshot!.Tables.Count);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Carol", preview.Rows.Single()["name"]);
            var committed = await coordinator.CommitAsync(connectionString, plan,
                preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Equal(3, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
            Assert.Equal(1, await ScalarAsync(connectionString,
                "SELECT COUNT(*) FROM users WHERE name = 'Carol'"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossTableSource_SourceValueOrSchemaDriftRejectsApprovedInsert(bool schemaDrift)
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            await ExecuteAsync(connectionString,
                "INSERT INTO other_users (id, name) VALUES (1, 'Carol')");
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, CrossTableSql);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(connectionString, plan, Approval,
                TestContext.Current.CancellationToken, version);
            if (schemaDrift)
            {
                await ExecuteAsync(connectionString,
                    "CREATE INDEX idx_other_users_name ON other_users(name)");
                await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, Approval,
                    TestContext.Current.CancellationToken, version));
            }
            else
            {
                await ExecuteAsync(connectionString,
                    "UPDATE other_users SET name = 'Mallory' WHERE id = 1");
                var rejected = await coordinator.CommitAsync(connectionString, plan,
                    preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
                Assert.False(rejected.Committed);
            }
            Assert.Equal(2, await ScalarAsync(connectionString,
                "SELECT COUNT(*) FROM users"));
        });
    }

    [Fact]
    public async Task CrossTableSource_SecondTableRowCountOverflowDenied()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            await ExecuteAsync(connectionString,
                "INSERT INTO other_users (id, name) VALUES (1, 'Carol'), (2, 'Carol'), (3, 'Carol')");
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString,
                "INSERT INTO main.users (name) SELECT name FROM main.other_users WHERE name = 'Carol'");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(connectionString,
                "SELECT COUNT(*) FROM users"));
        });
    }

    [Fact]
    public async Task SourceRowsChangedAfterApproval_RevalidationRollsBackWithoutInserting()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, InsertSql);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);

            await ExecuteAsync(connectionString, "UPDATE users SET name = 'Mallory' WHERE id = 1");
            var rejected = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.False(rejected.Committed);
            Assert.Contains("source row", rejected.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
        });
    }

    [Theory]
    [InlineData("INSERT INTO main.users (name) SELECT name FROM main.users")]
    [InlineData("INSERT INTO main.users (name) SELECT name FROM main.users WHERE id = 1 OR 1 = 1")]
    [InlineData("INSERT INTO main.users (name) SELECT upper(name) FROM main.users WHERE id = 1")]
    [InlineData("INSERT INTO main.users (name) SELECT name FROM other_users WHERE id = 1")]
    [InlineData("INSERT INTO main.users (name) SELECT name FROM main.other_users AS s WHERE id = 1")]
    [InlineData("INSERT INTO main.users (name) SELECT upper(name) FROM main.other_users WHERE id = 1")]
    [InlineData("INSERT INTO main.users (name) SELECT name FROM main.other_users WHERE id = 1 OR 1 = 1")]
    public async Task UnsupportedOrUnboundedSource_FailsClosedBeforePreview(string sql)
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                CreatePlanAsync(provider, snapshot, connectionString, sql));
            Assert.Equal(2, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
        });
    }

    [Fact]
    public async Task NativeSqliteSerializableTransaction_ReservesWriterBeforeSourceRevalidation()
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var provider = new SqliteProvider();
            await using var first = provider.Connections.Create(connectionString);
            await first.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await first.BeginTransactionAsync(
                IsolationLevel.Serializable, TestContext.Current.CancellationToken);

            // A second writer cannot get between the source recheck and the
            // approved INSERT when the provider begins an IMMEDIATE transaction.
            await using var rival = new SqliteConnection(connectionString);
            await rival.OpenAsync(TestContext.Current.CancellationToken);
            await using var rivalWrite = rival.CreateCommand();
            rivalWrite.CommandText = "UPDATE users SET name = 'hijack' WHERE id = 1";
            rivalWrite.CommandTimeout = 1;
            var denied = await Assert.ThrowsAsync<SqliteException>(() =>
                rivalWrite.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Equal(5, denied.SqliteErrorCode);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, await ScalarAsync(connectionString,
                "SELECT COUNT(*) FROM users WHERE name = 'Alice'"));
        });
    }

    [Fact]
    public async Task ForgedOrModifiedSourcePlan_CannotPassCoordinatorValidation()
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, InsertSql);

            Assert.Throws<InvalidOperationException>(() => DmlCoordinator.ValidatePlan(
                plan with { NativeInsertSelectProof = null }));
            Assert.Throws<InvalidOperationException>(() => DmlCoordinator.ValidatePlan(
                plan with { RowIdentityColumns = ["name", "id"] }));
            Assert.Throws<InvalidOperationException>(() => DmlCoordinator.ValidatePlan(
                plan with { MaxAffectedRows = 0 }));
        });
    }

    [Fact]
    public async Task ExcessSourceRows_RefusesApprovalBeforeAnyInsert()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            await ExecuteAsync(connectionString,
                "INSERT INTO users (id, name) VALUES (3, 'Alice'), (4, 'Alice')");
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(
                provider, snapshot, connectionString,
                "INSERT INTO main.users (name) SELECT name FROM main.users WHERE name = 'Alice'");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(4, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
        });
    }

    [Fact]
    public async Task CrossTableInsertSelect_ReturningTwoColumns_EmitsCommittedRowsAfterApprovedSourceRecheck()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            await ExecuteAsync(connectionString,
                "INSERT INTO other_users (id, name) VALUES (1, 'Carol')");
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString,
                "INSERT INTO main.users (name) SELECT name FROM main.other_users WHERE id = 1 RETURNING id, name",
                useVerifiedProfile: true);
            Assert.True(plan.MutationCommand.ReturnsRows);
            Assert.Equal(2, plan.SchemaSnapshot!.Tables.Count);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Carol", preview.Rows.Single()["name"]);
            Assert.DoesNotContain(preview.Rows.Single().Keys,
                column => string.Equals(column, "id", StringComparison.OrdinalIgnoreCase));

            var committed = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            var returned = Assert.Single(committed.ReturnedRows);
            Assert.Equal(3L, Convert.ToInt64(returned["id"]));
            Assert.Equal("Carol", returned["name"]);
            Assert.Equal(3, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task CrossTableReturning_SourceValueDrift_DoesNotLeakOrCommitReturnedRows()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            await ExecuteAsync(connectionString,
                "INSERT INTO other_users (id, name) VALUES (1, 'Carol')");
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString,
                "INSERT INTO main.users (name) SELECT name FROM main.other_users WHERE id = 1 RETURNING id, name",
                useVerifiedProfile: true);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(connectionString, "UPDATE other_users SET name = 'Mallory' WHERE id = 1");
            var rejected = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.False(rejected.Committed);
            Assert.Equal(0, rejected.AffectedRows);
            Assert.Empty(rejected.ReturnedRows);
            Assert.Equal(2, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
        });
    }

    [Theory]
    [InlineData("RETURNING *")]
    [InlineData("RETURNING upper(name)")]
    [InlineData("RETURNING id, id")]
    public async Task CrossTableInsertSelect_NonDirectOrDuplicatedReturning_FailsClosed(string returning)
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() => CreatePlanAsync(
                provider, snapshot, connectionString,
                "INSERT INTO main.users (name) SELECT name FROM main.other_users WHERE id = 1 " + returning,
                useVerifiedProfile: true));
            Assert.Equal(2, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
        });
    }

    private static async Task<ValidatedDmlPlan> CreatePlanAsync(
        SqliteProvider provider, NativeDmlSchemaSnapshotService snapshot,
        string connectionString, string sql, bool useVerifiedProfile = false)
    {
        await using var connection = provider.Connections.Create(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var profile = useVerifiedProfile
            ? RuntimeServerProfileVerifier.Capture(SqlAgentToolType.Sqlite, connection).TargetProfile
            : null;
        var parsed = profile is null
            ? CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite)
            : CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite, profile);
        var schema = await snapshot.CaptureAsync(
            connection, SqlAgentToolType.Sqlite, parsed, TestContext.Current.CancellationToken);
        Assert.NotNull(schema);
        var context = new SqlPlanValidationContext(
            Policy, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "main.users", "main.other_users" })
            .WithSchemaSnapshot(schema);
        return await new DmlPlanFactory(provider.Metadata).CreateWithMetadataConnectionAsync(
            connection, connectionString, parsed, SqlAgentToolType.Sqlite, context,
            maxAffectedRows: 2, cancellationToken: TestContext.Current.CancellationToken,
            targetProfile: profile);
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var file = Path.Combine(Path.GetTempPath(), $"hsa-insert-select-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={file};Pooling=False";
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var version = connection.ServerVersion.Trim();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                    INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob');
                    CREATE TABLE other_users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await test(connectionString, version);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
