using Xunit;

namespace SqlAgent.Test.Services;

public class CoreBooleanProjectionCapabilityTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_BooleanLiteralProjection_FailsAtCapabilityBoundary(SqlAgentToolType provider)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT TRUE FROM users",
            SqlAgentToolType.Postgres,
            provider));

        Assert.Contains("expression.boolean_select", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OracleRegexProjection_FailsAtCapabilityBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT REGEXP_LIKE(name, '^A') FROM users",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Oracle));

        Assert.Contains("expression.boolean_select", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerRegexProjectionAtCompatibility170_RemainsSupported()
    {
        var command = Compile(
            "SELECT REGEXP_LIKE(name, '^A') FROM users",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.MsSqlServer,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer,
                ServerVersion: new Version(17, 0),
                CompatibilityLevel: 170));

        Assert.Contains("REGEXP_LIKE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CORE_REGEX_MATCH", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OracleRegexPredicate_RemainsSupported()
    {
        var command = Compile(
            "SELECT id FROM users WHERE REGEXP_LIKE(name, '^A')",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Oracle);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
    }

    [Fact]
    public void Compile_BooleanCaseProjectionForOracle_FailsAtCapabilityBoundary()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT CASE WHEN id = 1 THEN TRUE ELSE FALSE END FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle));

        Assert.Contains("expression.boolean_select", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_NumericCaseProjectionForOracle_RemainsSupported()
    {
        var command = Compile(
            "SELECT CASE WHEN id = 1 THEN 1 ELSE 0 END FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);
}
