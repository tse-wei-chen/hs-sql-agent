using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreConcatCapabilityTests
{
    [Theory]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Translated, "CONCAT(")]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported, " || ")]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Supported, " || ")]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Supported, " || ")]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Supported, " || ")]
    public void NativeTargetSyntax_MatchesPublishedConcatCapability(
        SqlAgentToolType targetProvider,
        SqlCapabilityStatus expectedStatus,
        string expectedSql)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(targetProvider).Capabilities,
            item => item.Id == "expression.concat");
        var command = CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT first_name || last_name FROM users",
                SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("concat-contract-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Equal(expectedStatus, capability.Status);
        Assert.Contains(expectedSql, command.Sql, StringComparison.OrdinalIgnoreCase);
        if (targetProvider == SqlAgentToolType.MySQL)
            Assert.DoesNotContain(" || ", command.Sql, StringComparison.Ordinal);
        else
            Assert.DoesNotContain("CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
