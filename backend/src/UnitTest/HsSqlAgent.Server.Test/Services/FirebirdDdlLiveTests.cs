using System.Data.Common;
using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// G4 RED: only a real Firebird 5 database can authorize a CREATE TABLE.
/// Static compilation and an approval alone are never physical proof.
/// </summary>
public sealed class FirebirdDdlLiveTests
{
    private sealed class Connections : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new FbConnection(connectionString);
    }

    private sealed class Approver(
        Func<DdlApprovalRequest, CancellationToken, ValueTask<DdlApprovalDecision>> callback)
        : IDdlApprovalProvider
    {
        public ValueTask<DdlApprovalDecision> RequestAsync(
            DdlApprovalRequest request, CancellationToken token = default) => callback(request, token);
    }

    private static ISqlProvider Provider()
    {
        var mock = new Mock<ISqlProvider>(MockBehavior.Strict);
        mock.SetupGet(x => x.Type).Returns(SqlAgentToolType.Firebird);
        mock.SetupGet(x => x.Connections).Returns(new Connections());
        return mock.Object;
    }

    private static FirebirdDdlRuntime Runtime(string guid) => new(
        new FirebirdDdlExecutionOptions
        {
            Enabled = true,
            AllowCreateTable = true,
            AllowedDatabaseGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { guid },
            PolicyRevision = "firebird-g4-test"
        });

    private static IDdlApprovalProvider Approval(DdlApprovalState state) =>
        new Approver((request, _) => ValueTask.FromResult(
            new DdlApprovalDecision(state, request.RequestId, request.ApprovalFingerprint,
                "human:independent-reviewer")));

    [Fact]
    public async Task DisabledAndUnsupportedDdlFailBeforeAnyConnection()
    {
        var mock = new Mock<ISqlProvider>(MockBehavior.Strict);
        var actor = new DdlActorContext("mcp-key:1", "db-management:2", "configured-db");
        var disabled = await new FirebirdDdlRuntime().RunAsync(
            mock.Object, "unused", "CREATE TABLE G4_TEST(ID INT)", actor,
            false, null, TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, disabled.Status);
        mock.VerifyNoOtherCalls();

        mock.SetupGet(x => x.Type).Returns(SqlAgentToolType.Firebird);
        foreach (var sql in new[]
        {
            "DROP TABLE G4_TEST",
            "CREATE TABLE G4_TEST(ID INT); DROP TABLE G4_TEST",
            "CREATE TABLE G4_TEST(ID INT PRIMARY KEY)",
            "ALTER TABLE G4_TEST ADD OTHER INT",
            "CREATE INDEX G4_IDX ON G4_TEST(ID)",
            "CREATE TABLE APP.G4_TEST(ID INT)"
        })
        {
            var denied = await Runtime("not-a-real-guid").RunAsync(
                mock.Object, "unused", sql, actor, true, null,
                TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Rejected, denied.Status);
        }
        mock.VerifyGet(x => x.Connections, Times.Never);
    }

    [Fact]
    public async Task LivePreviewApprovalCommitAndExternalSchemaDriftAreIsolated()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_FIREBIRD_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Firebird DDL Host live tests require TEST_FIREBIRD_CONNECTION.");
            Assert.Skip("Firebird 5 required.");
        }

        var token = TestContext.Current.CancellationToken;
        await using var db = new FbConnection(connectionString);
        await db.OpenAsync(token);
        var guid = Convert.ToString(await ScalarAsync(db,
            "SELECT MON$GUID FROM MON$DATABASE", token))!.Trim();
        Assert.False(string.IsNullOrWhiteSpace(guid));
        var actor = new DdlActorContext("mcp-key:1", "db-management:2", "configured-db");
        var runtime = Runtime(guid);
        var provider = Provider();
        var table = "FBG4_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var drift = "FBG4_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var statement = "CREATE TABLE " + table + "(ID INT NOT NULL)";
        try
        {
            var preview = await runtime.RunAsync(provider, connectionString, statement,
                actor, true, null, token);
            Assert.Equal(DdlRuntimeStatus.Preview, preview.Status);
            Assert.False(preview.CommitConfirmed);
            Assert.Equal(0, Convert.ToInt32(await ScalarAsync(db,
                "SELECT COUNT(*) FROM RDB$RELATIONS WHERE RDB$RELATION_NAME = '" + table + "'", token)));

            var rejected = await runtime.RunAsync(provider, connectionString, statement,
                actor, false, Approval(DdlApprovalState.Rejected), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, rejected.Status);

            var forged = await runtime.RunAsync(provider, connectionString, statement,
                actor, false, new Approver((request, _) => ValueTask.FromResult(
                    new DdlApprovalDecision(DdlApprovalState.Approved, request.RequestId,
                        new string('0', 64), "human:reviewer"))), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, forged.Status);

            var committed = await runtime.RunAsync(provider, connectionString, statement,
                actor, false, Approval(DdlApprovalState.Approved), token);
            Assert.Equal(DdlRuntimeStatus.Committed, committed.Status);
            Assert.True(committed.CommitConfirmed);
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(db,
                "SELECT COUNT(*) FROM RDB$RELATIONS WHERE RDB$RELATION_NAME = '" + table + "'", token)));
            var repeated = await runtime.RunAsync(provider, connectionString, statement,
                actor, false, Approval(DdlApprovalState.Approved), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, repeated.Status);

            var concurrent = await runtime.RunAsync(provider, connectionString,
                "CREATE TABLE " + drift + "(ID INT NOT NULL)", actor, false,
                new Approver(async (request, ct) =>
                {
                    await using var outsider = new FbConnection(connectionString);
                    await outsider.OpenAsync(ct);
                    await ExecuteAsync(outsider,
                        "CREATE TABLE " + drift + "(OTHER INT)", ct);
                    return new DdlApprovalDecision(DdlApprovalState.Approved, request.RequestId,
                        request.ApprovalFingerprint, "human:independent-reviewer");
                }), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, concurrent.Status);
            Assert.Equal("ddl.schema_drift", concurrent.ErrorCode);
        }
        finally
        {
            foreach (var target in new[] { table, drift })
            {
                if (Convert.ToInt32(await ScalarAsync(db,
                    "SELECT COUNT(*) FROM RDB$RELATIONS WHERE RDB$RELATION_NAME = '" + target + "'",
                    CancellationToken.None)) > 0)
                    await ExecuteAsync(db, "DROP TABLE \"" + target + "\"", CancellationToken.None);
            }
        }
    }

    private static async Task<object?> ScalarAsync(
        FbConnection db, string sql, CancellationToken token)
    {
        await using var command = new FbCommand(sql, db);
        return await command.ExecuteScalarAsync(token);
    }

    private static async Task ExecuteAsync(FbConnection db, string sql, CancellationToken token)
    {
        await using var command = new FbCommand(sql, db);
        await command.ExecuteNonQueryAsync(token);
    }
}
