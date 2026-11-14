using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlQuotedIdentifierLoweringTests
{
    [Fact]
    public void Compile_PostgresUpdate_PreservesQuotedDotsSpacesAndPredicateIdentifier()
    {
        var command = Compile(
            "UPDATE \"Order.Detail\" SET \"Line Item\" = 1 WHERE \"Key.Col\" = 2",
            SqlAgentToolType.Postgres);

        Assert.Contains("UPDATE \"Order.Detail\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"Line Item\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"Key.Col\"", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Order\".\"Detail\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_PostgresDelete_PreservesQuotedDotInTargetAndPredicate()
    {
        var command = Compile(
            "DELETE FROM \"Order.Detail\" WHERE \"Key.Col\" = 2",
            SqlAgentToolType.Postgres);

        Assert.Contains("DELETE FROM \"Order.Detail\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"Key.Col\"", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Order\".\"Detail\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_PostgresInsert_PreservesQuotedDotTargetAndSpacedColumn()
    {
        var command = Compile(
            "INSERT INTO \"Order.Detail\" (\"Line Item\") VALUES (1)",
            SqlAgentToolType.Postgres);

        Assert.Contains("INSERT INTO \"Order.Detail\"", command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"Line Item\"", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Order\".\"Detail\"", command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "\"mixedtable\"", "\"mixedcolumn\"")]
    [InlineData(SqlAgentToolType.Oracle, "\"MIXEDTABLE\"", "\"MIXEDCOLUMN\"")]
    [InlineData(SqlAgentToolType.Firebird, "\"MIXEDTABLE\"", "\"MIXEDCOLUMN\"")]
    public void Compile_UnquotedDmlIdentifiers_FollowProviderFolding(
        SqlAgentToolType provider,
        string expectedTable,
        string expectedColumn)
    {
        var command = Compile(
            "UPDATE MixedTable SET MixedColumn = 1 WHERE Id = 2",
            provider);

        Assert.Contains(expectedTable, command.Sql, StringComparison.Ordinal);
        Assert.Contains(expectedColumn, command.Sql, StringComparison.Ordinal);
    }

    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType provider) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, provider),
            provider,
            new SqlPlanValidationContext("policy-v1"));
}
