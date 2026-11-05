using HsSqlAgent.SqlCore;
using Microsoft.Data.Sqlite;
using Npgsql;
using System.Data.Common;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G10/Q02 native GROUPS frames: peer groups, not physical ROWS or numeric RANGE.
/// The same compiled SQL is executed against actual PostgreSQL 16 / SQLite.
/// </summary>
public sealed class SqlCoreWindowGroupsFrameTests
{
    private const string GroupsQuery =
        "SELECT id, SUM(amount) OVER (ORDER BY grp " +
        "GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW) AS peer_total " +
        "FROM sqlcore_group_frames ORDER BY id";

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_GroupsFrame_PreservesNativePeerUnit(SqlAgentToolType provider)
    {
        var compiled = Compile(GroupsQuery, provider, provider);
        Assert.Contains("GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW",
            compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ROWS BETWEEN", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RANGE BETWEEN", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Parse_UnprovenSourceGroupsFrame_FailsAtSourceSyntax(SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(GroupsQuery, source));
        Assert.Contains("window.groups", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_UnprovenTargetGroupsFrame_FailsClosed(SqlAgentToolType target)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(GroupsQuery, SqlAgentToolType.Postgres, target));
        Assert.Contains("window.groups", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, 10, 0, 11, 0)]
    [InlineData(SqlAgentToolType.Sqlite, 3, 27, 3, 28)]
    public void Compile_ExplicitOlderSourceOrTargetVersion_RemainsFailClosed(
        SqlAgentToolType provider, int oldMajor, int oldMinor, int newMajor, int newMinor)
    {
        var older = new SqlProviderCapabilityProfile(provider, new Version(oldMajor, oldMinor));
        var newer = new SqlProviderCapabilityProfile(provider, new Version(newMajor, newMinor));
        Assert.Equal(SqlCapabilityStatus.Rejected,
            Assert.Single(SqlCapabilityMatrix.ForProvider(provider, older).Capabilities,
                item => item.Id == "window.groups").Status);
        Assert.Equal(SqlCapabilityStatus.Supported,
            Assert.Single(SqlCapabilityMatrix.ForProvider(provider, newer).Capabilities,
                item => item.Id == "window.groups").Status);

        var context = new SqlPlanValidationContext("g10-groups-version-proof-v1");
        var policy = new SqlExecutionPlanPolicy();
        var oldTarget = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(GroupsQuery, provider, provider,
                context, policy, older));
        Assert.Contains("window.groups", oldTarget.Message, StringComparison.OrdinalIgnoreCase);

        var oldSource = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(GroupsQuery, provider, provider,
                context, policy, older, newer));
        Assert.Contains("window.groups", oldSource.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_GroupsWithoutOrdering_FailsClosed(SqlAgentToolType provider)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT SUM(amount) OVER (GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW) " +
            "FROM sqlcore_group_frames", provider, provider));
        Assert.Contains("window.groups.order_by", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_BackwardsGroupsBounds_FailsClosed(SqlAgentToolType provider)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT SUM(amount) OVER (ORDER BY grp " +
            "GROUPS BETWEEN 1 FOLLOWING AND CURRENT ROW) FROM sqlcore_group_frames",
            provider, provider));
        Assert.Contains("logically after", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public async Task NativeDatabase_GroupsFrame_AgreesWithPeerGroupSemantics(
        SqlAgentToolType provider)
    {
        var postgresConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (provider == SqlAgentToolType.Postgres && string.IsNullOrWhiteSpace(postgresConnection))
        {
            Assert.Skip("Actual PostgreSQL required; sqlagent-test CI runs with PostgreSQL 16.");
            return;
        }

        await using DbConnection connection = provider == SqlAgentToolType.Postgres
            ? new NpgsqlConnection(postgresConnection)
            : new SqliteConnection("Data Source=:memory:");
        var cancellationToken = TestContext.Current.CancellationToken;
        await connection.OpenAsync(cancellationToken);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                "CREATE TEMP TABLE sqlcore_group_frames (id INTEGER, grp INTEGER, amount INTEGER);" +
                "INSERT INTO sqlcore_group_frames (id, grp, amount) VALUES " +
                "(1,1,2),(2,1,3),(3,2,7),(4,3,11);";
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }

        var compiled = Compile(GroupsQuery, provider, provider);
        await using var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        foreach (var bound in compiled.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = bound.Name;
            parameter.Value = bound.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var totals = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            totals.Add(Convert.ToInt64(reader.GetValue(1)));

        // The first two equal-order peers must observe the same whole group.
        Assert.Equal(new long[] { 5, 5, 12, 18 }, totals);
    }

    private static CompiledSqlCommand Compile(
        string sql, SqlAgentToolType source, SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source), target,
            new SqlPlanValidationContext("g10-groups-frame-v1"),
            new SqlExecutionPlanPolicy());
}
