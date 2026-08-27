using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreBooleanCasePredicateTests
{
    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_BooleanSearchedCasePredicate_UsesNumericTruthLowering(
        SqlAgentToolType provider)
    {
        var command = Compile(
            "SELECT id FROM users WHERE CASE WHEN id = 1 THEN TRUE ELSE FALSE END",
            provider);

        Assert.Contains("CASE WHEN", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THEN 1 ELSE 0 END", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("END = 1", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FALSE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(1, command.Parameters[0].Value);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_BooleanSimpleCasePredicate_PreservesSingleOperandEvaluation(
        SqlAgentToolType provider)
    {
        var command = Compile(
            "SELECT id FROM users WHERE CASE ABS(id) " +
            "WHEN 1 THEN TRUE WHEN 2 THEN FALSE ELSE FALSE END",
            provider);

        Assert.Equal(
            1,
            CountOccurrences(command.Sql, "ABS(", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("END = 1", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FALSE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal(1, command.Parameters[0].Value);
        Assert.Equal(2, command.Parameters[1].Value);
    }

    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType provider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            provider,
            new SqlPlanValidationContext(
                "boolean-case-predicate-v1",
                new HashSet<string>(
                    ["users"],
                    StringComparer.OrdinalIgnoreCase)),
            new SqlExecutionPlanPolicy());

    private static int CountOccurrences(
        string value,
        string token,
        StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, comparison)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
