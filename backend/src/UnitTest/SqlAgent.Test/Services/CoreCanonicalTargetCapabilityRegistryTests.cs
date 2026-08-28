using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCanonicalTargetCapabilityRegistryTests
{
    [Theory]
    [InlineData(
        "SELECT NTH_VALUE(id, 1) OVER (ORDER BY id) FROM users",
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MsSqlServer,
        "function.nth_value")]
    [InlineData(
        "SELECT JSON_EXTRACT(payload, '$.id') FROM events",
        SqlAgentToolType.MySQL,
        SqlAgentToolType.Oracle,
        "json_extract")]
    [InlineData(
        "SELECT REGEXP_LIKE(name, '^a') FROM users",
        SqlAgentToolType.MySQL,
        SqlAgentToolType.Sqlite,
        "regex_match")]
    [InlineData(
        "SELECT DATEADD(WEEK, 1, created_at) FROM orders",
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Postgres,
        "WEEK")]
    public void Compile_TargetCapabilityFamilyRegistry_PreservesProviderRejections(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        string expected)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, sourceDialect, targetProvider));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "SELECT NTILE(0) OVER (ORDER BY id) FROM users",
        SqlAgentToolType.Postgres,
        "NTILE bucket count")]
    [InlineData(
        "SELECT NTH_VALUE(id, 0) OVER (ORDER BY id) FROM users",
        SqlAgentToolType.Postgres,
        "NTH_VALUE index")]
    public void Compile_PositiveIntegerLiteralRegistry_PreservesFunctionValidation(
        string sql,
        SqlAgentToolType targetProvider,
        string expected)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, SqlAgentToolType.Postgres, targetProvider));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("canonical-target-capability-registry-v1"),
            new SqlExecutionPlanPolicy());
}
