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
    [InlineData(17, 0, 169, SqlCapabilityStatus.Rejected)]
    [InlineData(17, 0, 170, SqlCapabilityStatus.Translated)]
    [InlineData(16, 0, 170, SqlCapabilityStatus.Rejected)]
    public void Matrix_SqlServerRegex_RequiresVersionAndCompatibilityProfile(
        int major,
        int minor,
        int compatibilityLevel,
        SqlCapabilityStatus expectedStatus)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MsSqlServer,
                SqlServerProfile(major, minor, compatibilityLevel)).Capabilities,
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
            "ServerVersion 17.0",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "compatibility level 170",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerRegex_AtVersion17Compatibility170_ReachesNativeFunction()
    {
        var command = Compile(
            SqlAgentToolType.MsSqlServer,
            SqlServerProfile(17, 0, 170));

        Assert.Contains(
            "REGEXP_LIKE",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "CORE_REGEX_MATCH",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerRegex_AtVersion16Compatibility170_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                SqlAgentToolType.MsSqlServer,
                SqlServerProfile(16, 0, 170)));

        Assert.Contains(
            "ServerVersion 17.0",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "name ~ '^A'", "~")]
    [InlineData(SqlAgentToolType.MySQL, "name REGEXP '^A'", "REGEXP_LIKE")]
    [InlineData(SqlAgentToolType.Oracle, "REGEXP_LIKE(name, '^A')", "REGEXP_LIKE")]
    public void ProvenRegexSourceStillChecksAndRendersDeepNestedQueries(
        SqlAgentToolType provider, string predicate, string nativeMarker)
    {
        // No AST scan is needed to re-confirm a ProvenCapability, but the
        // native Regex shape must still survive parse, bind, normalization,
        // target capability, policy and native rendering.
        var sql = $"SELECT id FROM users WHERE {predicate}";
        for (var i = 0; i < 16; i++)
            sql = $"SELECT r{i}.id FROM ({sql}) r{i}";
        var command = SqlCoreFacade.CompileQuery(
            sql, provider, provider, new SqlPlanValidationContext("regex-proof-fastpath-v1"),
            new SqlExecutionPlanPolicy(100));
        Assert.Contains(nativeMarker, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "name ~ '^A'")]
    [InlineData(SqlAgentToolType.MySQL, "name REGEXP '^A'")]
    [InlineData(SqlAgentToolType.Oracle, "REGEXP_LIKE(name, '^A')")]
    public void ProvenSourceRegexCannotBypassRejectedTargetCapability(
        SqlAgentToolType provider, string predicate)
    {
        // Source admission is NOT target authorization. SQLite regex is
        // rejected even when the source dialect can prove the expression.
        var result = SqlCoreFacade.TryCompileQuery(
            $"SELECT id FROM users WHERE {predicate}",
            provider, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("regex-proof-target-reject-v1"),
            new SqlExecutionPlanPolicy(100));
        Assert.False(result.Success);
        Assert.Contains("function.regex_match", result.ErrorMessage,
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
        int major,
        int minor,
        int compatibilityLevel) =>
        new(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(major, minor),
            CompatibilityLevel: compatibilityLevel);
}
