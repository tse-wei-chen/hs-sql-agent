using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreJsonPathCapabilityTests
{
    [Theory]
    [InlineData("SELECT JSON_EXTRACT(payload, path) FROM events")]
    [InlineData("SELECT JSON_SET(payload, path, 'x') FROM events")]
    public void Compile_DynamicJsonPath_FailsAtCapabilityBoundary(string sql)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            sql,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL));

        Assert.Contains("json.path.constant", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT JSON_EXTRACT(payload, '$') FROM events")]
    [InlineData("SELECT JSON_EXTRACT(payload, '$.items[0].name') FROM events")]
    [InlineData("SELECT JSON_EXTRACT(payload, '$.items[*].name') FROM events")]
    public void Compile_JsonPathOutsidePortablePropertyChain_FailsBeforeLowering(string sql)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            sql,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("json.path.property_chain", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_JsonPropertyChain_RemainsSupportedForPostgres()
    {
        var command = Compile(
            "SELECT JSON_EXTRACT(payload, '$.user.name') FROM events",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
