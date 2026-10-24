using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlRendererValuesBudgetTests
{
    public static IEnumerable<object[]> SixDialects()
        => Enum.GetValues<SqlAgentToolType>().Select(x => new object[] { x });

    [Fact]
    public void SqlServerRowNumberPaging_PreservesPredicateThenPagingParameterOrder()
    {
        const string sql = "SELECT u.id FROM users u "
            + "JOIN users v ON u.id = v.id "
            + "WHERE v.id = 11 ORDER BY u.id OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY";
        var command = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("bounded-sqlserver-offset-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("ROW_NUMBER() OVER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[results_wrapper]", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new long[] { 11, 6, 15 },
            command.Parameters.Select(p => Convert.ToInt64(p.Value)));
    }

    [Fact]
    public void SqlServerSetRowNumberPaging_PreservesBranchThenPagingParameterOrder()
    {
        const string sql = "SELECT id FROM users WHERE id = 11 "
            + "UNION ALL SELECT id FROM users WHERE id = 22 "
            + "ORDER BY 1 OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY";
        var command = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("bounded-sqlserver-set-offset-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("ROW_NUMBER() OVER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION ALL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new long[] { 11, 22, 6, 15 },
            command.Parameters.Select(p => Convert.ToInt64(p.Value)));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void SetPagingWrapper_StreamsUnionBodyAndPreservesBranchParameterOrder(
        SqlAgentToolType target)
    {
        const string sql =
            "SELECT id FROM users WHERE id = 11 "
            + "UNION ALL SELECT id FROM users WHERE id = 22 "
            + "ORDER BY 1 LIMIT 7 OFFSET 3";
        var command = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("streamed-set-tail-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("UNION ALL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(command.Parameters.Length >= 2);
        Assert.Equal(new long[] { 11, 22 },
            command.Parameters.Take(2).Select(p => Convert.ToInt64(p.Value)));
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void MultiRowInsert_RendersParametersInRowMajorOrder(SqlAgentToolType dialect)
    {
        var values = string.Join(", ",
            Enumerable.Range(1, 64).Select(i => $"({i}, {i + 1000})"));
        var sql = "INSERT INTO users (id, parent_id) VALUES " + values;
        var result = SqlCoreFacade.CompileDml(
            sql, dialect, dialect,
            new SqlPlanValidationContext("bounded-values-render-v1"));
        var actual = result.Parameters.Select(parameter => Convert.ToInt64(parameter.Value));
        var expected = Enumerable.Range(1, 64).SelectMany(i => new long[] { i, i + 1000 });
        Assert.Equal(expected, actual);
        Assert.NotEmpty(result.Sql);
    }
}
