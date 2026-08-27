using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreConcatCapabilityTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Supported)]
    public void Matrix_MatchesConcatTargetSyntax(
        SqlAgentToolType provider,
        SqlCapabilityStatus expected)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "expression.concat");

        Assert.Equal(expected, capability.Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "CONCAT(")]
    [InlineData(SqlAgentToolType.MsSqlServer, " + ")]
    public void Compile_QueryConcat_UsesTranslatedTargetSyntax(
        SqlAgentToolType targetProvider,
        string expected)
    {
        var command = CompileQuery(targetProvider);

        Assert.Contains(expected, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" || ", command.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_QueryConcat_UsesNativePipes(
        SqlAgentToolType targetProvider)
    {
        var command = CompileQuery(targetProvider);

        Assert.Contains(" || ", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CONCAT(", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "CONCAT(")]
    [InlineData(SqlAgentToolType.MsSqlServer, " + ")]
    [InlineData(SqlAgentToolType.Postgres, " || ")]
    public void Compile_DmlConcat_UsesSameTargetContract(
        SqlAgentToolType targetProvider,
        string expected)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET display_name = first_name || last_name WHERE id = 1",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("concat-test"),
            new DmlCompilationPolicy());

        Assert.Contains(expected, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlRawPipesWithoutSessionContract_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT first_name || last_name FROM users",
                    SqlAgentToolType.MySQL),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("concat-test"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("PIPES_AS_CONCAT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sql_mode", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileQuery(SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT first_name || last_name FROM users",
                SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("concat-test"),
            new SqlExecutionPlanPolicy());
}
