using HsSqlAgent.SqlCore;
using Microsoft.Data.Sqlite;
using Npgsql;
using System.Data.Common;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G10/Q02: EXCLUDE CURRENT ROW must remove only the physical current row,
/// not its ORDER BY peer group. Only native PostgreSQL 11+ and SQLite 3.28+
/// are allowed to compile this window-frame contract.
/// </summary>
public sealed class SqlCoreWindowExclusionFrameTests
{
    private const string ExclusionQuery =
        "SELECT id, SUM(amount) OVER (ORDER BY grp " +
        "GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE CURRENT ROW) AS peer_without_self, " +
        "COUNT(amount) OVER (ORDER BY id " +
        "ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE CURRENT ROW) AS prior_count " +
        "FROM sqlcore_exclusion_frames ORDER BY id";

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_Exclusion_PreservesBothNativeFrames(SqlAgentToolType provider)
    {
        var compiled = Compile(ExclusionQuery, provider, provider);
        Assert.Contains("GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE CURRENT ROW",
            compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE CURRENT ROW",
            compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Parse_UnprovenExclusionSource_FailsClosed(SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(ExclusionQuery, source));
        Assert.Contains("window.exclude", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_UnprovenExclusionTarget_FailsClosed(SqlAgentToolType target)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(ExclusionQuery, SqlAgentToolType.Postgres, target));
        Assert.Contains("window.exclude", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, 10, 0, 11, 0)]
    [InlineData(SqlAgentToolType.Sqlite, 3, 27, 3, 28)]
    public void Compile_ExplicitOldVersions_FailAtSourceAndTarget(
        SqlAgentToolType provider, int oldMajor, int oldMinor, int newMajor, int newMinor)
    {
        var oldProfile = new SqlProviderCapabilityProfile(provider, new Version(oldMajor, oldMinor));
        var newProfile = new SqlProviderCapabilityProfile(provider, new Version(newMajor, newMinor));
        Assert.Equal(SqlCapabilityStatus.Rejected,
            Assert.Single(SqlCapabilityMatrix.ForProvider(provider, oldProfile).Capabilities,
                item => item.Id == "window.exclude.current_row").Status);
        Assert.Equal(SqlCapabilityStatus.Supported,
            Assert.Single(SqlCapabilityMatrix.ForProvider(provider, newProfile).Capabilities,
                item => item.Id == "window.exclude.current_row").Status);
        var context = new SqlPlanValidationContext("g10-window-exclusion-version-v1");
        var policy = new SqlExecutionPlanPolicy();
        var oldTarget = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(ExclusionQuery, provider, provider,
                context, policy, oldProfile));
        Assert.Contains("window.exclude", oldTarget.Message, StringComparison.OrdinalIgnoreCase);
        var oldSource = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(ExclusionQuery, provider, provider,
                context, policy, oldProfile, newProfile));
        Assert.Contains("window.exclude", oldSource.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("EXCLUDE GROUP")]
    [InlineData("EXCLUDE TIES")]
    [InlineData("EXCLUDE NO OTHERS")]
    public void Parse_UnmodeledExclusions_NeverDowngradeToDefault(string unsupported)
    {
        var query = "SELECT SUM(amount) OVER (ORDER BY grp " +
            "GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW " + unsupported +
            ") FROM sqlcore_exclusion_frames";
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(query, SqlAgentToolType.Postgres));
        Assert.Contains("window.exclude", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Parse_ExclusionWithoutFrame_IsNotImplicitlyAccepted(SqlAgentToolType provider)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT SUM(amount) OVER (ORDER BY grp EXCLUDE CURRENT ROW) FROM sqlcore_exclusion_frames",
                provider));
        Assert.Contains("window.exclude", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public async Task LivePeerRows_ExclusionRemovesOnlyCurrentRow(SqlAgentToolType provider)
    {
        var pgConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (provider == SqlAgentToolType.Postgres && string.IsNullOrWhiteSpace(pgConnection))
        {
            Assert.Skip("Live PostgreSQL required; sqlagent-test CI uses PostgreSQL 16.");
            return;
        }

        await using DbConnection connection = provider == SqlAgentToolType.Postgres
            ? new NpgsqlConnection(pgConnection)
            : new SqliteConnection("Data Source=:memory:");
        var ct = TestContext.Current.CancellationToken;
        await connection.OpenAsync(ct);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                "CREATE TEMP TABLE sqlcore_exclusion_frames (id INTEGER, grp INTEGER, amount INTEGER);" +
                "INSERT INTO sqlcore_exclusion_frames (id,grp,amount) VALUES " +
                "(1,1,2),(2,1,3),(3,2,7),(4,3,11);";
            await setup.ExecuteNonQueryAsync(ct);
        }

        var compiled = Compile(ExclusionQuery, provider, provider);
        await using var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        foreach (var bound in compiled.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = bound.Name;
            parameter.Value = bound.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        var peerTotals = new List<long>();
        var priorCounts = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            peerTotals.Add(Convert.ToInt64(reader.GetValue(1)));
            priorCounts.Add(Convert.ToInt64(reader.GetValue(2)));
        }
        Assert.Equal(new long[] { 3, 2, 5, 7 }, peerTotals);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, priorCounts);
    }

    private static CompiledSqlCommand Compile(
        string sql, SqlAgentToolType source, SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source), target,
            new SqlPlanValidationContext("g10-window-exclusion-v1"),
            new SqlExecutionPlanPolicy());
}
