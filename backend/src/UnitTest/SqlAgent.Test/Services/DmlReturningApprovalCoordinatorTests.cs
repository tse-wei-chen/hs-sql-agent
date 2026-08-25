using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlReturningApprovalCoordinatorTests
{
    [Fact]
    public async Task Commit_InsertReturning_MaterializesRowsAndPreservesApprovedCount()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hs-sql-agent-returning-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        try
        {
            await CreateUsersTableAsync(connectionString);
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.Sqlite,
                ServerVersion: new Version(3, 35));
            var parsed = CoreSqlTextParser.ParseDml(
                "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob') RETURNING id, name",
                SqlAgentToolType.Sqlite,
                profile);
            var command = CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("policy-returning-v1"),
                targetProfile: profile);
            var plan = CreateInsertPlan(command, expectedRows: 2);
            var coordinator = new DmlCoordinator(new SqliteConnectionFactory());

            var preview = await coordinator.PreviewAsync(
                connectionString,
                plan,
                TestContext.Current.CancellationToken);
            var result = await coordinator.CommitAsync(
                connectionString,
                plan,
                preview.Challenge,
                TestContext.Current.CancellationToken);

            Assert.True(command.ReturnsRows);
            Assert.True(result.Committed);
            Assert.Equal(2, result.AffectedRows);
            Assert.Equal(2, result.ReturnedRows.Length);
            Assert.Equal(1L, Convert.ToInt64(result.ReturnedRows[0]["id"]));
            Assert.Equal("Alice", result.ReturnedRows[0]["name"]);
            Assert.Equal(2L, Convert.ToInt64(result.ReturnedRows[1]["id"]));
            Assert.Equal("Bob", result.ReturnedRows[1]["name"]);
            Assert.Equal(2, await CountUsersAsync(connectionString));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Commit_ReturnedRowCountMismatch_RollsBackInsteadOfCommitting()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hs-sql-agent-returning-mismatch-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        try
        {
            await CreateUsersTableAsync(connectionString);
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.Sqlite,
                ServerVersion: new Version(3, 35));
            var parsed = CoreSqlTextParser.ParseDml(
                "INSERT INTO users (id, name) VALUES (1, 'Alice') RETURNING id",
                SqlAgentToolType.Sqlite,
                profile);
            var command = CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("policy-returning-v1"),
                targetProfile: profile);
            var plan = CreateInsertPlan(command, expectedRows: 2);
            var coordinator = new DmlCoordinator(new SqliteConnectionFactory());

            var preview = await coordinator.PreviewAsync(
                connectionString,
                plan,
                TestContext.Current.CancellationToken);
            var result = await coordinator.CommitAsync(
                connectionString,
                plan,
                preview.Challenge,
                TestContext.Current.CancellationToken);

            Assert.False(result.Committed);
            Assert.Equal(0, result.AffectedRows);
            Assert.Empty(result.ReturnedRows);
            Assert.Contains("row count changed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await CountUsersAsync(connectionString));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static ValidatedDmlPlan CreateInsertPlan(
        HsSqlAgent.SqlCore.Core.Compilation.CompiledSqlCommand command,
        int expectedRows)
    {
        const string policyVersion = "policy-returning-v1";
        var rows = Enumerable.Range(1, expectedRows)
            .Select(index => ImmutableDictionary.CreateRange<string, object?>(
                StringComparer.OrdinalIgnoreCase,
                [
                    new KeyValuePair<string, object?>("id", index),
                    new KeyValuePair<string, object?>("name", $"row-{index}")
                ]))
            .ToImmutableArray();

        return new ValidatedDmlPlan(
            DmlOperation.Insert,
            "users",
            command,
            MatchQueryCommand: null,
            RowIdentityColumns: ImmutableArray<string>.Empty,
            RowIdentityAssurance: DmlRowIdentityAssurance.CountOnly,
            PlanFingerprint: DmlFingerprintService.ComputePlanFingerprint(command, policyVersion),
            PolicyVersion: policyVersion,
            ApprovalTtl: TimeSpan.FromMinutes(5),
            MaxAffectedRows: expectedRows,
            ApprovalMode: DmlApprovalMode.InsertValues,
            InsertRows: rows);
    }

    private static async Task CreateUsersTableAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountUsersAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private sealed class SqliteConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new SqliteConnection(connectionString);
    }
}
