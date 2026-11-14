using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.Data.SqlClient;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Actual SQL Server 2022 physical source and target, no mock catalog or locks.</summary>
public sealed class DmlSqlServerInsertSelectApprovalTests
{
    private const string Approval = "sqlserver2022-physical-source-m09-approval-v1";


    [Theory]
    [InlineData("null", "Dave", 1)]
    [InlineData("not-null", "Carol", 1)]
    [InlineData("mixed", "Carol,Dave", 2)]
    public async Task NativeNullableColumnPredicate_SqlServerRealApprovalAndActualIdentity(
        string kind, string names, int affected)
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            await ExecuteAsync(cs, $"ALTER TABLE dbo.[{source}] ADD note NVARCHAR(16) NULL");
            await ExecuteAsync(cs, $"UPDATE dbo.[{source}] SET note = 'marked' WHERE id = 1");
            var predicate = kind switch
            {
                "null" => "note IS NULL",
                "not-null" => "note IS NOT NULL",
                _ => "(note IS NULL OR id = 1) AND name IS NOT NULL"
            };
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) OUTPUT INSERTED.id, INSERTED.name " +
                $"SELECT name FROM dbo.[{source}] WHERE {predicate}");
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
            Assert.Equal(2 + affected, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData(false, "between")]
    [InlineData(false, "and")]
    [InlineData(false, "or")]
    [InlineData(true, "between")]
    [InlineData(true, "and")]
    [InlineData(true, "or")]
    public async Task BoundedBetween_RealSqlServerRows_ApproveExactSourceAndReturnIdentity(
        bool selfSource, string kind)
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var from = selfSource ? target : source;
            var names = selfSource ? new[] { "Alice", "Bob" } : new[] { "Carol", "Dave" };
            var condition = kind switch
            {
                "between" => "id BETWEEN 1 AND 2",
                "and" => $"id BETWEEN 1 AND 2 AND name IN ('{names[0]}','{names[1]}')",
                _ => "id BETWEEN 1 AND 1 OR id = 2"
            };
            var sql = $"INSERT INTO dbo.[{target}] (name) OUTPUT INSERTED.id, INSERTED.name " +
                $"SELECT name FROM dbo.[{from}] WHERE {condition}";
            var plan = await CreatePlanAsync(provider, native, cs, target, from, sql);
            Assert.Equal(selfSource ? 1 : 2, plan.SchemaSnapshot!.Tables.Count);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(cs, plan, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.Equal(2, preview.AffectedRows);
            Assert.Equal(names, preview.Rows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            Assert.All(preview.Rows, row => Assert.False(row.ContainsKey("id")));
            var result = await coordinator.CommitAsync(cs, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(result.Committed);
            Assert.Equal(2, result.AffectedRows);
            Assert.Equal(names, result.ReturnedRows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { 3, 4 }, result.ReturnedRows.Select(x => Convert.ToInt32(x["id"])).OrderBy(x => x).ToArray());
            Assert.Equal(4, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task BoundedBetween_SourceDriftAfterApproval_RejectsWithoutReturningOrWriting()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) OUTPUT INSERTED.id " +
                $"SELECT name FROM dbo.[{source}] WHERE id BETWEEN 1 AND 2");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(cs, plan, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.Equal(2, preview.AffectedRows);
            await ExecuteAsync(cs, $"UPDATE dbo.[{source}] SET name = 'Eve' WHERE id = 2");
            var rejected = await coordinator.CommitAsync(cs, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.False(rejected.Committed);
            Assert.Empty(rejected.ReturnedRows);
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task BoundedBetween_ThirdMatchingRow_EnforcesFiniteAffectedRowCap()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            await ExecuteAsync(cs, $"INSERT INTO dbo.[{source}] (id, name) VALUES (3, 'Eve')");
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id BETWEEN 1 AND 3");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Theory]
    [InlineData(false, "in")]
    [InlineData(false, "or")]
    [InlineData(false, "mixed")]
    [InlineData(true, "in")]
    [InlineData(true, "or")]
    [InlineData(true, "mixed")]
    public async Task BoundedInOr_SourceRows_AreApprovedAndInsertedOnceWithRealIdentity(
        bool selfSource, string predicateKind)
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var from = selfSource ? target : source;
            var predicate = predicateKind switch
            {
                "in" => "id IN (1, 2)",
                "or" => "id = 1 OR id = 2",
                _ => " (id = 1 OR id = 2) AND name IN ('" +
                    (selfSource ? "Alice', 'Bob'" : "Carol', 'Dave'") + ")"
            };
            var sql = $"INSERT INTO dbo.[{target}] (name) OUTPUT INSERTED.id, INSERTED.name " +
                $"SELECT name FROM dbo.[{from}] WHERE {predicate}";
            var plan = await CreatePlanAsync(provider, native, cs, target, from, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal(selfSource ? 1 : 2, plan.SchemaSnapshot!.Tables.Count);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(2, preview.AffectedRows);
            var sourceNames = selfSource ? new[] { "Alice", "Bob" } : new[] { "Carol", "Dave" };
            Assert.Equal(sourceNames, preview.Rows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            Assert.All(preview.Rows, row => Assert.False(row.ContainsKey("id")));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
            var committed = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(2, committed.AffectedRows);
            Assert.Equal(sourceNames, committed.ReturnedRows.Select(x => (string)x["name"]!).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { 3, 4 }, committed.ReturnedRows.Select(x => Convert.ToInt32(x["id"])).OrderBy(x => x).ToArray());
            Assert.Equal(4, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task BoundedInOr_SourceValueChangedAfterApproval_RejectsBothRowsWithoutOutput()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) OUTPUT INSERTED.id " +
                $"SELECT name FROM dbo.[{source}] WHERE id IN (1,2)");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(2, preview.AffectedRows);
            await ExecuteAsync(cs, $"UPDATE dbo.[{source}] SET name = 'Eve' WHERE id = 2");
            var result = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.False(result.Committed);
            Assert.Empty(result.ReturnedRows);
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task BoundedInOr_ThreeMatchingSourceRows_AreDeniedAtPreview()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            await ExecuteAsync(cs, $"INSERT INTO dbo.[{source}] (id, name) VALUES (3, 'Eve')");
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id IN (1,2,3)");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeSqlServerOutput_ApprovedInsertSelect_ReturnsRealGeneratedTargetValues(
        bool selfSource, bool multipleColumns)
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var from = selfSource ? target : source;
            var expectedName = selfSource ? "Alice" : "Carol";
            var selectedOutput = multipleColumns ? "INSERTED.id, INSERTED.name" : "INSERTED.id";
            var sql = $"INSERT INTO dbo.[{target}] (name) OUTPUT {selectedOutput} " +
                $"SELECT name FROM dbo.[{from}] WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, cs, target, from, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal(selfSource ? 1 : 2, plan.SchemaSnapshot!.Tables.Count);
            Assert.True(plan.MutationCommand.ReturnsRows);

            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal(expectedName, Assert.Single(preview.Rows)["name"]);
            Assert.False(preview.Rows[0].ContainsKey("id"),
                "Preview approves actual source columns only; it cannot forge a generated target ID.");

            var committed = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            var row = Assert.Single(committed.ReturnedRows);
            Assert.Equal(3, Convert.ToInt32(row["id"]));
            if (multipleColumns) Assert.Equal(expectedName, row["name"]);
            else Assert.Single(row);
            Assert.Equal(1, await ScalarAsync(cs,
                $"SELECT COUNT(*) FROM dbo.[{target}] WHERE id = 3 AND name = '{expectedName}'"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task NativeSqlServerOutput_SourceDrift_DoesNotLeakOrCommitReturnedTargetRows()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) OUTPUT INSERTED.id, INSERTED.name " +
                $"SELECT name FROM dbo.[{source}] WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(cs, $"UPDATE dbo.[{source}] SET name = 'Eve' WHERE id = 1");
            var denied = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.False(denied.Committed);
            Assert.Empty(denied.ReturnedRows);
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Theory]
    [InlineData("OUTPUT INSERTED.*")]
    [InlineData("OUTPUT INSERTED.id AS created")]
    [InlineData("OUTPUT INSERTED.id, INSERTED.id")]
    public void NativeSqlServerOutput_NonDirectOrAmbiguousRowImage_IsRejectedByHost(string output)
    {
        var sql = $"INSERT INTO dbo.t (name) {output} SELECT name FROM dbo.s WHERE id = 1";
        try
        {
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer);
            var insert = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.InsertStatement>(parsed.Statement);
            Assert.ThrowsAny<Exception>(() => NativeInsertSelectSafety.RequireShape(
                insert, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer, 2));
        }
        catch (HsSqlAgent.SqlCore.Core.Compilation.SqlCompilationException)
        {
            // An F# source-grammar rejection is also a fail-closed outcome.
        }
    }

    [Fact]
    public async Task SelfSource_OnePhysicalTable_PreviewCommitsExactlyOnceWithGeneratedIdentity()
    {
        await WithTablesAsync(async (cs, target, _, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{target}] WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, cs, target, target, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Single(plan.SchemaSnapshot!.Tables);
            Assert.Equal("dbo." + target, plan.SchemaSnapshot.Tables[0].Name,
                ignoreCase: true);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Alice", Assert.Single(preview.Rows)["name"]);
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
            var committed = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Equal(3, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
            Assert.Equal(1, await ScalarAsync(cs,
                $"SELECT COUNT(*) FROM dbo.[{target}] WHERE id = 3 AND name = 'Alice'"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task SelfSource_InsertedRowsMatchingSamePredicate_AreNotRevisitedWithinOneInsert()
    {
        await WithTablesAsync(async (cs, target, _, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, target,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{target}] WHERE name = 'Alice'");
            Assert.Single(plan.SchemaSnapshot!.Tables);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Alice", Assert.Single(preview.Rows)["name"]);
            var commit = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(commit.Committed);
            Assert.Equal(1, commit.AffectedRows);
            Assert.Equal(3, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
            Assert.Equal(2, await ScalarAsync(cs,
                $"SELECT COUNT(*) FROM dbo.[{target}] WHERE name = 'Alice'"));
        });
    }

    [Fact]
    public async Task SelfSource_ReadCommittedTransaction_CannotAttestLivePhysicalSource()
    {
        await WithTablesAsync(async (cs, target, _, _) =>
        {
            var native = new NativeDmlSchemaSnapshotService();
            var provider = new MsSqlServerProvider();
            var parsed = CoreSqlTextParser.ParseDml(
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{target}] WHERE id = 1",
                SqlAgentToolType.MsSqlServer);
            await using var conn = provider.Connections.Create(cs);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            var snapshot = await native.CaptureAsync(
                conn, SqlAgentToolType.MsSqlServer, parsed, TestContext.Current.CancellationToken);
            Assert.NotNull(snapshot);
            Assert.Single(snapshot.Tables);
            await using var tx = await conn.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, TestContext.Current.CancellationToken);
            Assert.Null(await native.ReadCurrentSchemaDigestAsync(
                conn, tx, SqlAgentToolType.MsSqlServer, snapshot,
                TestContext.Current.CancellationToken));
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task SelfSource_ApprovedSourceChanged_RejectsWithoutAnyInsert()
    {
        await WithTablesAsync(async (cs, target, _, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, target,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{target}] WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(cs, $"UPDATE dbo.[{target}] SET name = 'Eve' WHERE id = 1");
            var denied = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.False(denied.Committed);
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task SelfSource_ApprovedIndexDrift_DeniesBeforeMutation()
    {
        await WithTablesAsync(async (cs, target, _, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, target,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{target}] WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(cs, $"CREATE INDEX [m09_self_name] ON dbo.[{target}] ([name])");
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task SelfSource_IdenticalDdlRecreation_InvalidatesOldPhysicalApproval()
    {
        await WithTablesAsync(async (cs, target, _, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, target,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{target}] WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(cs, $"DROP TABLE dbo.[{target}]");
            await ExecuteAsync(cs, $"CREATE TABLE dbo.[{target}] (id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, name NVARCHAR(64) NOT NULL)");
            await ExecuteAsync(cs, $"INSERT INTO dbo.[{target}] (name) VALUES ('Alice'),('Bob')");
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task SelfSource_OneTransactionLock_PreventsConcurrentPhysicalWriter()
    {
        await WithTablesAsync(async (cs, target, _, _) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var parsed = CoreSqlTextParser.ParseDml(
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{target}] WHERE id = 1",
                SqlAgentToolType.MsSqlServer);
            await using var conn = provider.Connections.Create(cs);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            var snapshot = await native.CaptureAsync(
                conn, SqlAgentToolType.MsSqlServer, parsed, TestContext.Current.CancellationToken);
            Assert.NotNull(snapshot);
            Assert.Single(snapshot.Tables);
            await using var tx = await conn.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, TestContext.Current.CancellationToken);
            try
            {
                Assert.Equal(snapshot.Sha256, await native.ReadCurrentSchemaDigestAsync(
                    conn, tx, SqlAgentToolType.MsSqlServer, snapshot,
                    TestContext.Current.CancellationToken));
                await using var other = new SqlConnection(cs);
                await other.OpenAsync(TestContext.Current.CancellationToken);
                await using var write = new SqlCommand(
                    $"UPDATE dbo.[{target}] SET name = 'hijack' WHERE id = 1", other);
                write.CommandTimeout = 1;
                var blocked = await Assert.ThrowsAsync<SqlException>(() =>
                    write.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
                Assert.True(blocked.Number is -2 or 1222,
                    $"Expected SQL Server lock timeout, got {blocked.Number}.");
            }
            finally
            {
                await tx.RollbackAsync(TestContext.Current.CancellationToken);
            }
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
            Assert.Equal(1, await ScalarAsync(cs,
                $"SELECT COUNT(*) FROM dbo.[{target}] WHERE id = 1 AND name = 'Alice'"));
        });
    }

    [Fact]
    public async Task SelfSource_ExcessMatchingRows_RejectsBeforeApprovalOrMutation()
    {
        await WithTablesAsync(async (cs, target, _, version) =>
        {
            await ExecuteAsync(cs, $"INSERT INTO dbo.[{target}] (name) VALUES ('Alice'),('Alice')");
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, target,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{target}] WHERE name = 'Alice'");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(4, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task TwoPhysicalTables_ApprovedSourceValues_CommitOneNativeInsert()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, cs, target, source, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal(2, plan.SchemaSnapshot!.Tables.Count);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Carol", Assert.Single(preview.Rows)["name"]);
            var committed = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Empty(committed.ReturnedRows);
            Assert.Equal(3, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task ApprovedSourceValueDrift_FailsClosedWithoutInserting()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(cs, $"UPDATE dbo.[{source}] SET name = 'Eve' WHERE id = 1");
            var result = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.False(result.Committed);
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task SourceIndexDrift_RejectsOldApprovalBeforeMutation()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(cs, $"CREATE INDEX [ix_source_name] ON dbo.[{source}] ([name])");
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task SameDefinitionAndValuesButRecreatedSource_InvalidatesOldApproval()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(cs, $"DROP TABLE dbo.[{source}]");
            await ExecuteAsync(cs, $"CREATE TABLE dbo.[{source}] (id INT NOT NULL PRIMARY KEY, name NVARCHAR(64) NOT NULL)");
            await ExecuteAsync(cs, $"INSERT INTO dbo.[{source}] (id,name) VALUES (1,'Carol'),(2,'Dave')");
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task SerializableFreshnessProof_PreventsConcurrentPhysicalSourceWriter()
    {
        await WithTablesAsync(async (cs, target, source, _) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var parsed = CoreSqlTextParser.ParseDml(
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1",
                SqlAgentToolType.MsSqlServer);
            await using var connection = provider.Connections.Create(cs);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var snapshot = await native.CaptureAsync(
                connection, SqlAgentToolType.MsSqlServer, parsed, TestContext.Current.CancellationToken);
            Assert.NotNull(snapshot);
            await using var tx = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, TestContext.Current.CancellationToken);
            try
            {
                Assert.Equal(snapshot.Sha256, await native.ReadCurrentSchemaDigestAsync(
                    connection, tx, SqlAgentToolType.MsSqlServer, snapshot,
                    TestContext.Current.CancellationToken));
                await using var rival = new SqlConnection(cs);
                await rival.OpenAsync(TestContext.Current.CancellationToken);
                await using var write = new SqlCommand(
                    $"UPDATE dbo.[{source}] SET name = 'hijack' WHERE id = 1", rival);
                write.CommandTimeout = 1;
                var denial = await Assert.ThrowsAsync<SqlException>(() =>
                    write.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
                Assert.True(denial.Number is -2 or 1222, $"Expected lock timeout, got SQL Server {denial.Number}.");
            }
            finally
            {
                await tx.RollbackAsync(TestContext.Current.CancellationToken);
            }
            Assert.Equal(1, await ScalarAsync(cs,
                $"SELECT count(*) FROM dbo.[{source}] WHERE id = 1 AND name = 'Carol'"));
        });
    }

    [Fact]
    public async Task PhysicalSourceTrigger_DeniesSnapshotBeforeApproval()
    {
        await WithTablesAsync(async (cs, target, source, _) =>
        {
            var trigger = "m09_tr_" + Guid.NewGuid().ToString("N")[..12];
            await ExecuteAsync(cs,
                $"CREATE TRIGGER dbo.[{trigger}] ON dbo.[{source}] AFTER UPDATE AS BEGIN SET NOCOUNT ON; END");
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var parsed = CoreSqlTextParser.ParseDml(
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1",
                SqlAgentToolType.MsSqlServer);
            await using var connection = provider.Connections.Create(cs);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Null(await native.CaptureAsync(
                connection, SqlAgentToolType.MsSqlServer, parsed, TestContext.Current.CancellationToken));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task MissingIndependentSourceGrant_DeniesBeforeExecutablePlan()
    {
        await WithTablesAsync(async (cs, target, source, _) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1";
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                CreatePlanAsync(provider, native, cs, target, source, sql, grantSource: false));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
        });
    }

    [Theory]
    [InlineData("INSERT INTO dbo.t (name) SELECT name FROM dbo.s")]
    [InlineData("INSERT INTO dbo.t (name) SELECT name FROM dbo.s WHERE id = 1 OR 1 = 1")]
    [InlineData("INSERT INTO dbo.t (name) SELECT upper(name) FROM dbo.s WHERE id = 1")]
    public void UnprovenHostShapes_DoNotGainSqlServerSourceApproval(string sql)
    {
        try
        {
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer);
            var insert = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.InsertStatement>(parsed.Statement);
            Assert.ThrowsAny<Exception>(() => NativeInsertSelectSafety.RequireShape(
                insert, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer, 2));
        }
        catch (HsSqlAgent.SqlCore.Core.Compilation.SqlCompilationException)
        {
            // A stricter F# syntax rejection is also fail-closed.
        }
    }

    private static async Task<ValidatedDmlPlan> CreatePlanAsync(
        MsSqlServerProvider provider, NativeDmlSchemaSnapshotService native, string cs,
        string target, string source, string sql, bool grantSource = true)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer);
        await using var connection = provider.Connections.Create(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var snapshot = await native.CaptureAsync(
            connection, SqlAgentToolType.MsSqlServer, parsed, TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        var grants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo." + target };
        if (grantSource) grants.Add("dbo." + source);
        return await new DmlPlanFactory(provider.Metadata).CreateWithMetadataConnectionAsync(
            connection, cs, parsed, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver2022-m09-v1", grants).WithSchemaSnapshot(snapshot),
            maxAffectedRows: 2, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task WithTablesAsync(Func<string, string, string, string, Task> action)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("M09 SQL Server requires actual TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Requires TEST_SQLSERVER_CONNECTION.");
        }
        var tag = Guid.NewGuid().ToString("N")[..12];
        var target = "m09_dst_" + tag;
        var source = "m09_src_" + tag;
        await ExecuteAsync(cs!, $"CREATE TABLE dbo.[{target}] (id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, name NVARCHAR(64) NOT NULL)");
        try
        {
            await ExecuteAsync(cs!, $"CREATE TABLE dbo.[{source}] (id INT NOT NULL PRIMARY KEY, name NVARCHAR(64) NOT NULL)");
            try
            {
                await ExecuteAsync(cs!, $"INSERT INTO dbo.[{target}] (name) VALUES ('Alice'),('Bob')");
                await ExecuteAsync(cs!, $"INSERT INTO dbo.[{source}] (id,name) VALUES (1,'Carol'),(2,'Dave')");
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync(TestContext.Current.CancellationToken);
                await action(cs!, target, source, conn.ServerVersion.Trim());
            }
            finally { await ExecuteAsync(cs!, $"DROP TABLE IF EXISTS dbo.[{source}]"); }
        }
        finally { await ExecuteAsync(cs!, $"DROP TABLE IF EXISTS dbo.[{target}]"); }
    }

    private static async Task ExecuteAsync(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(sql, conn);
        command.CommandTimeout = 10;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(sql, conn);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
