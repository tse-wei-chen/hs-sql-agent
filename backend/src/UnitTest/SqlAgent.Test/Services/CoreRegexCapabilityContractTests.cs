using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreRegexCapabilityContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Rejected)]
    public void Matrix_RegexCapability_WithoutRuntimeProfile_StaysAligned(
        SqlAgentToolType provider,
        SqlCapabilityStatus expectedStatus)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "regex.match");

        Assert.Equal(expectedStatus, capability.Status);
    }

    [Theory]
    [InlineData(169, SqlCapabilityStatus.Rejected)]
    [InlineData(170, SqlCapabilityStatus.Translated)]
    public void Matrix_SqlServerRegex_TracksCompatibilityProfile(
        int compatibilityLevel,
        SqlCapabilityStatus expectedStatus)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MsSqlServer,
                SqlServerProfile(compatibilityLevel)).Capabilities,
            item => item.Id == "regex.match");

        Assert.Equal(expectedStatus, capability.Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "~")]
    [InlineData(SqlAgentToolType.MySQL, "REGEXP_LIKE")]
    [InlineData(SqlAgentToolType.Oracle, "REGEXP_LIKE")]
    public void Compile_Regex_ReachesDeclaredNativeRenderer(
        SqlAgentToolType targetProvider,
        string expectedSql)
    {
        var command = Compile(targetProvider);

        Assert.Contains(
            expectedSql,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_Regex_ProviderWideRejections_FailAtCapabilityBoundary(
        SqlAgentToolType targetProvider)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(targetProvider));

        Assert.Contains(
            "function.regex_match",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerRegex_WithoutProfile_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(SqlAgentToolType.MsSqlServer));

        Assert.Contains(
            "compatibility level 170",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerRegex_AtCompatibility170_ReachesNativeFunction()
    {
        var command = Compile(
            SqlAgentToolType.MsSqlServer,
            SqlServerProfile(170));

        Assert.Contains(
            "REGEXP_LIKE",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "CORE_REGEX_MATCH",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM users WHERE REGEXP_LIKE(name, '^A')",
                SqlAgentToolType.Oracle),
            targetProvider,
            new SqlPlanValidationContext("regex-capability-contract-v1"),
            new SqlExecutionPlanPolicy(),
            targetProfile);

    private static SqlProviderCapabilityProfile SqlServerProfile(
        int compatibilityLevel) =>
        new(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(17, 0),
            CompatibilityLevel: compatibilityLevel);
}
