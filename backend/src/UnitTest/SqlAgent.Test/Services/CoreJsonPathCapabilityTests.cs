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
    [InlineData("SELECT JSON_EXTRACT(payload, '$.items[*].name') FROM events")]
    [InlineData("SELECT JSON_EXTRACT(payload, '$.items..name') FROM events")]
    public void Compile_JsonPathOutsidePortablePropertyChain_FailsBeforeLowering(string sql)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            sql,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("json.path.property_chain", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_JsonArrayIndexExtraction_LowersThroughTypedPathSegments()
    {
        var command = Compile(
            "SELECT JSON_EXTRACT(payload, '$.items[0].name') FROM events",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres);

        Assert.Contains("JSONB_EXTRACT_PATH", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$.items[0].name", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_JsonArrayIndexMutation_RemainsFailClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT JSON_SET(payload, '$.items[0].name', 'x') FROM events",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("json.path.mutation_array_index", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_JsonPathSimple_RemainsTranslatedForEveryProvider()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        {
            var capability = Assert.Single(
                SqlCapabilityMatrix.ForProvider(provider).Capabilities,
                item => item.Id == "json.path.simple");

            Assert.Equal(SqlCapabilityStatus.Translated, capability.Status);
            Assert.Contains(
                "constant paths",
                capability.Detail,
                StringComparison.OrdinalIgnoreCase);

            var arrayIndex = Assert.Single(
                SqlCapabilityMatrix.ForProvider(provider).Capabilities,
                item => item.Id == "json.path.array_index_extract");

            Assert.Equal(
                provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    ? SqlCapabilityStatus.Translated
                    : SqlCapabilityStatus.Rejected,
                arrayIndex.Status);
        }
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

    [Fact]
    public void Compile_StaticJsonPath_RemainsConstantAfterStructuralSpanAnnotation()
    {
        var command = Compile(
            "SELECT JSON_EXTRACT(payload, '$.id') FROM events",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL);

        Assert.Contains("JSON_EXTRACT", command.Sql, StringComparison.OrdinalIgnoreCase);
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
