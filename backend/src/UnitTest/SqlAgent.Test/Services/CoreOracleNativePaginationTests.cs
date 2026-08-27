using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreOracleNativePaginationTests
{
    [Fact]
    public void Compile_LimitWithoutOrderBy_UsesRowLimitingClauseWithoutSyntheticOrder()
    {
        var command = Compile("SELECT id FROM users LIMIT 10");

        Assert.DoesNotContain("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FETCH NEXT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt64(parameter.Value) == 0L);
        Assert.Contains(command.Parameters, parameter => Convert.ToInt64(parameter.Value) == 10L);
    }

    [Fact]
    public void Compile_OffsetWithoutOrderBy_UsesOffsetWithoutSyntheticOrder()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users OFFSET 5 ROWS",
            SqlAgentToolType.Oracle);
        var command = Compile(parsed);

        Assert.DoesNotContain("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FETCH NEXT", command.Sql, StringComparison.OrdinalIgnoreCase);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal(5L, Convert.ToInt64(parameter.Value));
    }

    [Fact]
    public void Compile_OffsetFetchWithoutOrderBy_UsesNativeRowLimitingClause()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
            SqlAgentToolType.Oracle);
        var command = Compile(parsed);

        Assert.DoesNotContain("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FETCH NEXT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(5L, Convert.ToInt64(command.Parameters[0].Value));
        Assert.Equal(10L, Convert.ToInt64(command.Parameters[1].Value));
    }

    private static CompiledSqlCommand Compile(string sql) =>
        Compile(CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres));

    private static CompiledSqlCommand Compile(ParsedStatement parsed) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("oracle-native-pagination-v1"),
            new SqlExecutionPlanPolicy());
}
