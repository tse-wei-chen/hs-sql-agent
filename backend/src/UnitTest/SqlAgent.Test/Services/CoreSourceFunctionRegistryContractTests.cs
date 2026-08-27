using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSourceFunctionRegistryContractTests
{
    [Theory]
    [InlineData(
        "SELECT DATEADD(DAY, 1) FROM orders",
        SqlAgentToolType.MsSqlServer,
        "DATEADD")]
    [InlineData(
        "SELECT DATEDIFF(created_at, completed_at) FROM orders",
        SqlAgentToolType.MsSqlServer,
        "DATEDIFF")]
    [InlineData(
        "SELECT DATEDIFF(DAY, created_at, completed_at) FROM orders",
        SqlAgentToolType.MySQL,
        "DATEDIFF")]
    [InlineData(
        "SELECT STRING_AGG(name) FROM users",
        SqlAgentToolType.Postgres,
        "STRING_AGG")]
    [InlineData(
        "SELECT GROUP_CONCAT(name, ',', ':') FROM users",
        SqlAgentToolType.Sqlite,
        "GROUP_CONCAT")]
    [InlineData(
        "SELECT LISTAGG(name, ',', ':') FROM users",
        SqlAgentToolType.Oracle,
        "LISTAGG")]
    [InlineData(
        "SELECT LIST(name, ',', ':') FROM users",
        SqlAgentToolType.Firebird,
        "LIST")]
    public void Compile_StaticSourceFunctionRules_RejectWrongArityBeforeNormalization(
        string sql,
        SqlAgentToolType sourceDialect,
        string functionName)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, sourceDialect, sourceDialect));

        Assert.Contains(functionName, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"source dialect {sourceDialect}",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "SELECT DATE_FORMAT(created_at, '%Y') FROM orders",
        SqlAgentToolType.Postgres,
        "DATE_FORMAT")]
    [InlineData(
        "SELECT FORMAT(created_at, 'yyyy') FROM orders",
        SqlAgentToolType.MySQL,
        "FORMAT")]
    [InlineData(
        "SELECT TO_DATE(value, 'YYYY-MM-DD') FROM records",
        SqlAgentToolType.MySQL,
        "TO_DATE")]
    [InlineData(
        "SELECT CHARINDEX('x', name) FROM users",
        SqlAgentToolType.Postgres,
        "CHARINDEX")]
    [InlineData(
        "SELECT LOCATE('x', name) FROM users",
        SqlAgentToolType.Postgres,
        "LOCATE")]
    [InlineData(
        "SELECT STRPOS(name, 'x') FROM users",
        SqlAgentToolType.MySQL,
        "STRPOS")]
    [InlineData(
        "SELECT INSTR(name, 'x') FROM users",
        SqlAgentToolType.MsSqlServer,
        "INSTR")]
    [InlineData(
        "SELECT JSON_EXTRACT(payload, '$.id') FROM events",
        SqlAgentToolType.Postgres,
        "JSON_EXTRACT")]
    [InlineData(
        "SELECT REGEXP_LIKE(name, '^a') FROM users",
        SqlAgentToolType.Postgres,
        "REGEXP_LIKE")]
    [InlineData(
        "SELECT GETDATE() FROM users",
        SqlAgentToolType.Postgres,
        "GETDATE")]
    [InlineData(
        "SELECT NOW() FROM users",
        SqlAgentToolType.MsSqlServer,
        "NOW")]
    [InlineData(
        "SELECT GROUP_CONCAT(name) FROM users",
        SqlAgentToolType.Postgres,
        "GROUP_CONCAT")]
    [InlineData(
        "SELECT LISTAGG(name, ',') FROM users",
        SqlAgentToolType.Postgres,
        "LISTAGG")]
    [InlineData(
        "SELECT LIST(name, ',') FROM users",
        SqlAgentToolType.Postgres,
        "LIST")]
    public void Compile_StaticSourceFunctionRules_RejectWrongDialectBeforeNormalization(
        string sql,
        SqlAgentToolType sourceDialect,
        string functionName)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, sourceDialect, sourceDialect));

        Assert.Contains(functionName, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"source dialect {sourceDialect}",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_CurrentTemporalSourceRules_RemainOwnedByDedicatedCapabilityContract()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT CURRENT_DATE FROM users",
                SqlAgentToolType.MsSqlServer,
                SqlAgentToolType.Postgres));

        Assert.Contains("CURRENT_DATE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Transact-SQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("source-function-registry-v1"),
            new SqlExecutionPlanPolicy());
}
